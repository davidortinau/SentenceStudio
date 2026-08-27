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
// Kept lowercase and separator-free to follow the existing azd parameter convention:
// feedbackhmackey maps predictably to the AZURE_FEEDBACKHMACKEY deployment input.
var feedbackhmackey = builder.AddParameter("feedbackhmackey", secret: true);

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
// Local run mode reads the AppHost's complete builder configuration, so appsettings.Development
// and user secrets retain the existing E2E workflow. Publish mode must not use either source:
// manifest values come only from explicit process environment variables supplied for deployment.
// Defaults stay fail-closed: off, baseline arm, and no cohort. The API additionally denies
// everyone when the cohort is empty, so setting Coach__Enabled alone still exposes nobody.
//
// appsettings.Development.json names the __dev_all__ cohort sentinel, which admits every
// authenticated user. That value is Development-only and enforced as such by the API:
// CoachOptionsValidator fails startup when it reaches a host that is not Development, and
// CoachAvailabilityPolicy ignores it there even if validation were bypassed. Forwarding it from a
// non-Development AppHost therefore stops the API from booting rather than exposing everyone.
var coachConfiguration = CoachConfigurationReader.ForExecutionMode(
    builder.Configuration,
    builder.ExecutionContext.IsPublishMode);
var coachEnvironmentResult = CoachConfigurationReader.ReadApiEnvironment(coachConfiguration);

// Defense-in-depth: warn about duplicate source indices so the operator can fix their config.
// The profile ID value is intentionally not logged — index is sufficient to locate the entry.
foreach (var dupIndex in coachEnvironmentResult.DuplicateAllowlistSourceIndices)
{
    Console.WriteLine(
        $"warn: Coach:AllowedUserProfileIds[{dupIndex}] is a duplicate of an earlier entry and was dropped.");
}

var api = builder.AddProject<SentenceStudio_Api>("api")
    .WithEnvironment("AI__OpenAI__Endpoint", aiEndpoint)
    .WithEnvironment("AI__OpenAI__Models__Fast", aiFastModel)
    .WithEnvironment("AI__OpenAI__Models__Reasoning", aiReasoningModel)
    .WithEnvironment("ElevenLabsKey", elevenlabskey)
    .WithEnvironment("Jwt__SigningKey", jwtkey)
    .WithEnvironment("GitHub__Pat", githubpat)
    .WithEnvironment("Feedback__HmacKey", feedbackhmackey)
    .WithReference(postgres)
    .WaitFor(postgres)
    // Injects ConnectionStrings__coach-keyring. The API reads it to persist the Data Protection
    // key ring; see CoachDataProtectionServiceCollectionExtensions. WaitFor keeps the API from
    // starting against an emulator that has not accepted connections yet, which would otherwise
    // leave it on the ephemeral ring for the life of the process.
    .WithReference(coachKeyRing)
    .WaitFor(coachKeyRing)
    .WithExternalHttpEndpoints();

// The reader always emits the fail-closed Enabled and Implementation values, then adds only
// explicitly configured pilot IDs and optional settings. Optional formats, ranges, and feature
// dependencies remain the API validators' responsibility so there is one validation authority.
foreach (var (environmentName, value) in coachEnvironmentResult.EnvironmentVariables)
{
    api = api.WithEnvironment(environmentName, value);
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

// var existingFoundryName = builder.AddParameter("existingFoundryName")
//     .WithDescription("The name of the existing Azure Foundry resource.");
// var existingFoundryResourceGroup = builder.AddParameter("existingFoundryResourceGroup")
//     .WithDescription("The resource group of the existing Azure Foundry resource.");

// var foundry = builder.AddAzureAIFoundry("foundry")
//     .AsExisting(existingFoundryName, existingFoundryResourceGroup);
