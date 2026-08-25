using Microsoft.Extensions.Configuration;
using Projects;
using SentenceStudio.AppHost;

var builder = DistributedApplication.CreateBuilder(args);

// NOTE: AddAzureContainerAppEnvironment was removed because it changes azd's
// manifest template generation to a format incompatible with existing azd env vars.
// When migrating to aspire deploy as primary, re-add:
//   builder.AddAzureContainerAppEnvironment("aca-env").WithAzdResourceNaming();
// and run `aspire deploy` instead of `azd deploy`.

var elevenlabskey = builder.AddParameter("elevenlabskey", secret: true);
var jwtkey = builder.AddParameter("jwtkey", secret: true);
var githubpat = builder.AddParameter("githubpat", secret: true);

// Azure AI Foundry (daortin-sstudio-eus2) — non-secret config for the server hosts, which
// authenticate to Foundry keyless via Entra (DefaultAzureCredential / managed identity).
// Injected as env vars so deployed containers get correct config regardless of the
// (git-ignored) appsettings.json baked into the image.
// eus2 account carries chat (gpt-5/gpt-5-mini) + realtime + transcribe in one resource;
// AzureResourceEndpoint() strips the /openai/v1 suffix to the bare resource root for AzureOpenAIClient.
const string aiEndpoint = "https://daortin-sstudio-eus2.openai.azure.com/openai/v1";
const string aiFastModel = "gpt-5-mini";
const string aiReasoningModel = "gpt-5";

// Managed Azure PostgreSQL Flexible Server in production;
// local Docker container for dev (RunAsContainer).
var dbUser = builder.AddParameter("dbUser");
var dbPassword = builder.AddParameter("dbPassword", secret: true);

// Optional named local data volume. Unset (the default) keeps stock Aspire behaviour: Aspire
// generates a volume name from the AppHost path, so every worktree starts on its own empty
// database. Setting it points this worktree at a volume you prepared yourself — typically a
// clone of an established local database made with scripts/clone-local-db-volume.sh, so testing
// starts with real vocabulary, plans, and history instead of a blank slate.
//
// Only ever name a volume you are willing to have a dev stack write to. The AppHost does not
// create, copy, or clean up volumes; that is the script's job, and it never overwrites one.
var localDbDataVolume = builder.Configuration["LocalDb:DataVolume"]?.Trim();

// Opt-in escape hatch for a genuinely disposable local database. It exists so that "I want an
// empty database" stays possible while "I forgot to say which database" stops being possible.
// See the guard below.
var allowEphemeralLocalDb = string.Equals(
    builder.Configuration["LocalDb:AllowEphemeralVolume"]?.Trim(),
    "true",
    StringComparison.OrdinalIgnoreCase);

var postgresServer = builder.AddAzurePostgresFlexibleServer("db")
    .WithPasswordAuthentication(dbUser, dbPassword)
    .RunAsContainer(c =>
    {
        // Pin local dev image to PostgreSQL 17 to keep compatibility with existing
        // persistent volumes created before Docker's PostgreSQL 18+ data-layout change.
        // WithImageTag must come before WithDataVolume: Aspire picks the container data
        // directory (/var/lib/postgresql/data for 17, /var/lib/postgresql for 18+) from the
        // tag configured at the time WithDataVolume is called.
        c.WithImageTag("17")
            .WithLifetime(ContainerLifetime.Persistent);

        if (string.IsNullOrWhiteSpace(localDbDataVolume))
        {
            // FAIL FAST. Stock Aspire behaviour here is WithDataVolume() with no name, which
            // derives a volume name from this AppHost's path. That is a silent, *plausible*
            // failure: the stack comes up, the webapp answers on https://localhost:7071, and the
            // database behind it is empty or belongs to some other lineage. Whoever is driving the
            // browser only finds out when their account "no longer exists".
            //
            // That is exactly how Captain's dave@ortinau.com environment kept disappearing: an
            // agent restarted the AppHost without the LocalDb__DataVolume prefix, Aspire attached
            // an auto-named volume, ContainerLifetime.Persistent kept that wrong container alive
            // for days, and the real database sat unmounted with LINKS 0.
            //
            // Refusing to boot is the whole point. An unbootable stack is a five-second fix; a
            // stack running on the wrong database costs a testing session and Captain's trust.
            if (!allowEphemeralLocalDb)
            {
                throw new InvalidOperationException(
                    """
                    Local database volume is not configured (LocalDb:DataVolume is empty).

                    Refusing to start, because the fallback is an auto-named volume derived from
                    this AppHost's path — which silently serves an EMPTY or UNRELATED database at
                    https://localhost:7071 while looking completely healthy.

                    Choose one:

                      1. Reuse your established local database (the normal path). Persist it once:
                           dotnet user-secrets set "LocalDb:DataVolume" "<volume-name>" \
                             --project src/SentenceStudio.AppHost
                         ...or for a single run:
                           LocalDb__DataVolume=<volume-name> aspire run

                      2. Agent / E2E run: NEVER point at the human's volume. Clone it first, and
                         run on isolated ports:
                           scripts/clone-local-db-volume.sh --source <established> \
                             --destination sentencestudio-agent-<task>-db-data ...
                           LocalDb__DataVolume=sentencestudio-agent-<task>-db-data aspire run

                      3. You genuinely want a throwaway empty database:
                           LocalDb__AllowEphemeralVolume=true aspire run

                    List candidates with:  docker volume ls | grep sentencestudio
                    Full guidance:         docs/local-dev-database-volumes.md
                    """);
            }

            c.WithDataVolume();
        }
        else
        {
            c.WithDataVolume(localDbDataVolume);
        }
    });

var postgres = postgresServer.AddDatabase("sentencestudio");

var redis = builder.AddRedis("cache");

var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator(azurite => azurite
        .WithDataVolume("sentencestudio-local-azurite-data")
        .WithLifetime(ContainerLifetime.Persistent))
    .AddBlobs("media");

// The Data Protection key ring for the API. It is a separate container from "media" on purpose:
// the ring is key material, so it gets its own container and can be given its own access policy
// without touching learner media.
//
// Locally this resolves to the Azurite emulator, which is the whole point — the default ASP.NET
// key ring is written inside the container image and is regenerated on every restart, so every
// protected coach row written before a restart becomes permanently unreadable. Persisting to a
// blob keeps the ring across restarts and across replicas.
var coachKeyRing = storage.AddBlobContainer("coach-keyring", "coach-dataprotection");

// --- Learning Coach flow-through configuration ---------------------------------------------
// The coach is read from the AppHost's own configuration (environment, user-secrets, or
// appsettings) and forwarded to the api resource, so a Coach E2E run under `aspire run` needs no
// source edit. Defaults stay fail-closed: off, baseline arm, and no cohort. The API additionally
// denies everyone when the cohort is empty, so setting Coach__Enabled alone still exposes nobody.
//
// appsettings.Development.json names the __dev_all__ cohort sentinel, which admits every
// authenticated user. That value is Development-only and enforced as such by the API:
// CoachOptionsValidator fails startup when it reaches a host that is not Development, and
// CoachAvailabilityPolicy ignores it there even if validation were bypassed. Forwarding it from a
// non-Development AppHost therefore stops the API from booting rather than exposing everyone.
var coachEnabled = ReadCoachEnabled(builder.Configuration);
var coachImplementation = ReadCoachImplementation(builder.Configuration);
var coachAllowlistResult = CoachConfigurationReader.ReadAllowedUserProfileIdsWithDiagnostics(builder.Configuration);
var coachAllowedUserProfileIds = coachAllowlistResult.Ids;

// Defense-in-depth: warn about duplicate source indices so the operator can fix their config.
// The profile ID value is intentionally not logged — index is sufficient to locate the entry.
foreach (var dupIndex in coachAllowlistResult.DuplicateSourceIndices)
{
    Console.WriteLine(
        $"warn: Coach:AllowedUserProfileIds[{dupIndex}] is a duplicate of an earlier entry and was dropped.");
}

// Optional coach settings, forwarded verbatim when the AppHost was given one and omitted
// otherwise, so the API keeps its own defaults. Format and range belong to the API's
// CoachOptionsValidator, which fails startup with a readable message; repeating those rules here
// would create a second copy to drift.
//
// Coach__AgentConfigVersion is the one an E2E run usually sets: the API treats a session written
// under a different agent config version as unresumable, so bumping it starts a fresh coach
// session without deleting any existing session or plan-revision history.
var coachOptionalSettings = new[]
{
    ("Coach:AgentConfigVersion", "Coach__AgentConfigVersion"),
    ("Coach:MaxOutputTokens", "Coach__MaxOutputTokens"),
    ("Coach:ReasoningEffort", "Coach__ReasoningEffort"),

    // Run budgets. One run is one learner turn, and the compiled defaults (10/day, 40/week) are
    // sized for a pilot learner, not for a verification pass that has to drive twelve write tools
    // and a failure matrix through the same account in one sitting. Forwarded for the same reason
    // as everything else here: the number belongs to the deployment, not to whichever default
    // happens to be compiled in. CoachOptionsValidator still bounds both (per-day ceiling, and
    // per-week never below per-day), so raising them here cannot produce an unbounded budget.
    ("Coach:MaxRunsPerDay", "Coach__MaxRunsPerDay"),
    ("Coach:MaxRunsPerWeek", "Coach__MaxRunsPerWeek"),

    // Data Protection key ring. Both are resource identifiers, not credentials: the key
    // identifier names the wrapping key and the client id names a managed identity, and neither
    // grants access on its own. They are forwarded as plain environment values for that reason,
    // while anything that is actually a secret continues to go through AddParameter(secret: true).
    // The API requires the key identifier in Production once durable coach content is on.
    ("Coach:DataProtection:KeyVaultKeyIdentifier", "Coach__DataProtection__KeyVaultKeyIdentifier"),
    ("Coach:DataProtection:ManagedIdentityClientId", "Coach__DataProtection__ManagedIdentityClientId"),

    // Durable content flags. These are the two switches that decide whether learner text is
    // written to disk at all, and therefore whether the API demands a durable, Key-Vault-wrapped
    // key ring before it will start in Production. Forwarded here so the decision is made by
    // deployment configuration rather than by whichever default happens to be compiled in.
    ("Coach:DurableHistory:Enabled", "Coach__DurableHistory__Enabled"),
    ("Coach:Memory:Enabled", "Coach__Memory__Enabled"),

    // Sam overlay UX and tool surface. The dependency chain is validated by
    // CoachOptionsValidator at API startup: SamOverlay requires DurableHistory,
    // SamReadTools requires SamOverlay, SamWriteTools requires SamReadTools.
    ("Coach:SamOverlay:Enabled", "Coach__SamOverlay__Enabled"),
    ("Coach:SamReadTools:Enabled", "Coach__SamReadTools__Enabled"),
    ("Coach:SamWriteTools:Enabled", "Coach__SamWriteTools__Enabled"),

    // The opportunity ledger and the learner-report control. Two switches, deliberately, and both
    // forwarded so either can be flipped by deployment configuration rather than by a redeploy:
    // automatic capture observes the server refusing itself, while a report is a learner spending
    // an action to disagree with a turn the server thought went fine, and turning the first off
    // must never discard the second.
    //
    // The operator review surface is NOT forwarded and must not be. It can decrypt learner
    // messages, CoachOpportunityOptionsValidator fails startup if it is enabled outside
    // Development, and an environment variable that could set it is an environment variable
    // somebody can set on the wrong host. The production reviewer path is the out-of-band digest —
    // see docs/sam-opportunity-digest.md.
    ("Coach:Opportunities:Enabled", "Coach__Opportunities__Enabled"),
    ("Coach:Opportunities:RetentionDays", "Coach__Opportunities__RetentionDays"),
    ("Coach:Reports:Enabled", "Coach__Reports__Enabled"),
    ("Coach:Reports:RetentionDays", "Coach__Reports__RetentionDays")
};

var api = builder.AddProject<SentenceStudio_Api>("api")
    .WithEnvironment("AI__OpenAI__Endpoint", aiEndpoint)
    .WithEnvironment("AI__OpenAI__Models__Fast", aiFastModel)
    .WithEnvironment("AI__OpenAI__Models__Reasoning", aiReasoningModel)
    .WithEnvironment("ElevenLabsKey", elevenlabskey)
    .WithEnvironment("Jwt__SigningKey", jwtkey)
    .WithEnvironment("GitHub__Pat", githubpat)
    .WithEnvironment("Coach__Enabled", coachEnabled ? "true" : "false")
    .WithEnvironment("Coach__Implementation", coachImplementation)
    .WithReference(postgres)
    .WaitFor(postgres)
    // Injects ConnectionStrings__coach-keyring. The API reads it to persist the Data Protection
    // key ring; see CoachDataProtectionServiceCollectionExtensions. WaitFor keeps the API from
    // starting against an emulator that has not accepted connections yet, which would otherwise
    // leave it on the ephemeral ring for the life of the process.
    .WithReference(coachKeyRing)
    .WaitFor(coachKeyRing)
    .WithExternalHttpEndpoints();

// Forwarded only when the AppHost was actually given pilot learners. An empty or absent value
// leaves the cohort unset, which the API treats as "nobody is in the pilot".
for (var i = 0; i < coachAllowedUserProfileIds.Count; i++)
{
    api = api.WithEnvironment($"Coach__AllowedUserProfileIds__{i}", coachAllowedUserProfileIds[i]);
}

foreach (var (configurationKey, environmentName) in coachOptionalSettings)
{
    var value = builder.Configuration[configurationKey]?.Trim();
    if (!string.IsNullOrWhiteSpace(value))
    {
        api = api.WithEnvironment(environmentName, value);
    }
}

// Email (production only -- dev mode uses ConsoleEmailSender automatically), applied to api:
//   .WithEnvironment("Email__SmtpHost", "<smtp-host>")
//   .WithEnvironment("Email__SmtpPort", "587")
//   .WithEnvironment("Email__FromAddress", "noreply@sentencestudio.app")
//   .WithEnvironment("Email__FromName", "SentenceStudio")
//   .WithEnvironment("Email__Username", "<smtp-user>")       // user-secrets
//   .WithEnvironment("Email__Password", "<smtp-password>")   // user-secrets

var webapp = builder.AddProject<SentenceStudio_WebApp>("webapp")
    .WithEnvironment("AI__OpenAI__Endpoint", aiEndpoint)
    .WithEnvironment("AI__OpenAI__Models__Fast", aiFastModel)
    .WithEnvironment("AI__OpenAI__Models__Reasoning", aiReasoningModel)
    .WithEnvironment("ElevenLabsKey", elevenlabskey)
    .WithEnvironment("Jwt__SigningKey", jwtkey)
    .WithReference(api)
    .WithReference(redis)
    .WithReference(postgres)
    .WaitFor(postgres)
    .WithExternalHttpEndpoints();

builder.AddProject<SentenceStudio_Marketing>("marketing")
    .WithExternalHttpEndpoints();

var workers = builder.AddProject<SentenceStudio_Workers>("workers")
    .WithEnvironment("AI__OpenAI__Endpoint", aiEndpoint)
    .WithEnvironment("AI__OpenAI__Models__Fast", aiFastModel)
    .WithEnvironment("AI__OpenAI__Models__Reasoning", aiReasoningModel)
    .WithEnvironment("ElevenLabsKey", elevenlabskey)
    .WithReference(api)
    .WithReference(postgres)
    .WithReference(redis)
    .WithReference(storage);

// Part-of-speech backfill, forwarded to the workers host only — never to the API, which has no
// business running a bulk classification chore. Every value is optional and forwarded verbatim
// only when present, so the default stays off with an empty allowlist. Bounds and the refusal to
// run without an allowlist live in the service, not here.
//
// Enabling requires naming profiles explicitly:
//   VocabularyPartOfSpeechBackfill__Enabled=true
//   VocabularyPartOfSpeechBackfill__UserProfileIds__0=<user_profile_id>
foreach (var (configurationKey, environmentName) in new[]
{
    ("VocabularyPartOfSpeechBackfill:Enabled", "VocabularyPartOfSpeechBackfill__Enabled"),
    ("VocabularyPartOfSpeechBackfill:BatchSize", "VocabularyPartOfSpeechBackfill__BatchSize"),
    ("VocabularyPartOfSpeechBackfill:MaxWords", "VocabularyPartOfSpeechBackfill__MaxWords")
})
{
    var value = builder.Configuration[configurationKey]?.Trim();
    if (!string.IsNullOrWhiteSpace(value))
    {
        workers = workers.WithEnvironment(environmentName, value);
    }
}

// The allowlist is an array, so forward each index the AppHost was actually given. An absent or
// blank entry is skipped, which leaves the allowlist empty and the backfill refusing to run.
for (var i = 0; i < 8; i++)
{
    var profileId = builder.Configuration[$"VocabularyPartOfSpeechBackfill:UserProfileIds:{i}"]?.Trim();
    if (string.IsNullOrWhiteSpace(profileId))
    {
        continue;
    }

    workers = workers.WithEnvironment($"VocabularyPartOfSpeechBackfill__UserProfileIds__{i}", profileId);
}

// MAUI clients and dev tunnels are local-dev only — excluded from Azure publish
if (builder.ExecutionContext.IsRunMode)
{
    var openaikey = builder.AddParameter("openaikey", secret: true);
    var syncfusionkey = builder.AddParameter("syncfusionkey", secret: true);

    var maccatalyst = builder.AddMauiProject("maccatalyst", "../SentenceStudio.MacCatalyst/SentenceStudio.MacCatalyst.csproj");

    maccatalyst.AddMacCatalystDevice()
        .WithEnvironment("SyncfusionKey", syncfusionkey)
        .WithEnvironment("AI__OpenAI__ApiKey", openaikey)
        .WithEnvironment("ElevenLabsKey", elevenlabskey)
        .WithReference(api);

    var windows = builder.AddMauiProject("windows", "../SentenceStudio.Windows/SentenceStudio.Windows.csproj");

    windows.AddWindowsDevice()
        .WithEnvironment("SyncfusionKey", syncfusionkey)
        .WithEnvironment("AI__OpenAI__ApiKey", openaikey)
        .WithEnvironment("ElevenLabsKey", elevenlabskey)
        .WithReference(api);

    // Dev tunnel for mobile platforms (iOS/Android can't reach localhost directly).
    // References the api "http" endpoint — the AppHost only activates the api project's
    // first (http) launch profile, so GetEndpoint("https") would fail to resolve and, under
    // Aspire 13.5+, cascade-fail every downstream resource that references the tunnel.
    var publicDevTunnel = builder.AddDevTunnel("devtunnel-public")
        .WithAnonymousAccess()
        .WithReference(api.GetEndpoint("http"));

    // Android
    var android = builder.AddMauiProject("android", "../SentenceStudio.Android/SentenceStudio.Android.csproj");

    android.AddAndroidEmulator()
        .WithOtlpDevTunnel()
        .WithEnvironment("SyncfusionKey", syncfusionkey)
        .WithEnvironment("AI__OpenAI__ApiKey", openaikey)
        .WithEnvironment("ElevenLabsKey", elevenlabskey)
        .WithReference(api, publicDevTunnel);

    // iOS
    var ios = builder.AddMauiProject("ios", "../SentenceStudio.iOS/SentenceStudio.iOS.csproj");

    ios.AddiOSSimulator()
        .WithOtlpDevTunnel()
        .WithEnvironment("SyncfusionKey", syncfusionkey)
        .WithEnvironment("AI__OpenAI__ApiKey", openaikey)
        .WithEnvironment("ElevenLabsKey", elevenlabskey)
        .WithReference(api, publicDevTunnel);
}

builder.Build().Run();

// --- Learning Coach configuration readers ---------------------------------------------------
// Both readers default to the safe value when the setting is absent, and fail loudly when it is
// present but unusable. A typo in a kill switch must never quietly read as "off" during an E2E
// run, and it must never quietly read as "on" either.

static bool ReadCoachEnabled(IConfiguration configuration)
{
    var raw = configuration["Coach:Enabled"];

    if (string.IsNullOrWhiteSpace(raw))
    {
        return false;
    }

    if (!bool.TryParse(raw.Trim(), out var enabled))
    {
        throw new InvalidOperationException(
            "Coach:Enabled must be 'true' or 'false'. Set Coach__Enabled on the AppHost, or leave it unset to keep the coach off.");
    }

    return enabled;
}

static string ReadCoachImplementation(IConfiguration configuration)
{
    var raw = configuration["Coach:Implementation"];

    if (string.IsNullOrWhiteSpace(raw))
    {
        return "baseline";
    }

    var implementation = raw.Trim().ToLowerInvariant();

    if (implementation is not ("baseline" or "harness"))
    {
        throw new InvalidOperationException(
            "Coach:Implementation must be 'baseline' or 'harness'. Leave Coach__Implementation unset to keep the plain baseline agent arm.");
    }

    return implementation;
}

// var existingFoundryName = builder.AddParameter("existingFoundryName")
//     .WithDescription("The name of the existing Azure Foundry resource.");
// var existingFoundryResourceGroup = builder.AddParameter("existingFoundryResourceGroup")
//     .WithDescription("The resource group of the existing Azure Foundry resource.");

// var foundry = builder.AddAzureAIFoundry("foundry")
//     .AsExisting(existingFoundryName, existingFoundryResourceGroup);
