using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var openaikey = builder.AddParameter("openaikey", secret: true);
var elevenlabskey = builder.AddParameter("elevenlabskey", secret: true);
var jwtkey = builder.AddParameter("jwtkey", secret: true);

var postgres = builder.AddPostgres("db")
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume()
    .AddDatabase("sentencestudio");

var redis = builder.AddRedis("cache");

var storage = builder.AddAzureStorage("storage")
    .RunAsEmulator()
    .AddBlobs("media");

var api = builder.AddProject<SentenceStudio_Api>("api")
    .WithEnvironment("AI__OpenAI__ApiKey", openaikey)
    .WithEnvironment("ElevenLabsKey", elevenlabskey)
    .WithEnvironment("Jwt__SigningKey", jwtkey)
    .WithReference(postgres)
    .WaitFor(postgres)
    .WithExternalHttpEndpoints();
    // Email (production only -- dev mode uses ConsoleEmailSender automatically):
    //   .WithEnvironment("Email__SmtpHost", "<smtp-host>")
    //   .WithEnvironment("Email__SmtpPort", "587")
    //   .WithEnvironment("Email__FromAddress", "noreply@sentencestudio.app")
    //   .WithEnvironment("Email__FromName", "SentenceStudio")
    //   .WithEnvironment("Email__Username", "<smtp-user>")       // user-secrets
    //   .WithEnvironment("Email__Password", "<smtp-password>")   // user-secrets

var webapp = builder.AddProject<SentenceStudio_WebApp>("webapp")
    .WithEnvironment("ElevenLabsKey", elevenlabskey)
    .WithEnvironment("Jwt__SigningKey", jwtkey)
    .WithReference(api)
    .WithReference(redis)
    .WithReference(postgres)
    .WaitFor(postgres)
    .WithExternalHttpEndpoints();

builder.AddProject<SentenceStudio_Marketing>("marketing")
    .WithExternalHttpEndpoints();

builder.AddProject<SentenceStudio_Workers>("workers")
    .WithEnvironment("AI__OpenAI__ApiKey", openaikey)
    .WithEnvironment("ElevenLabsKey", elevenlabskey)
    .WithReference(api)
    .WithReference(postgres)
    .WithReference(redis)
    .WithReference(storage);

// MAUI clients and dev tunnels are local-dev only — excluded from Azure publish
if (builder.ExecutionContext.IsRunMode)
{
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

    // Dev tunnel for mobile platforms (iOS/Android can't reach localhost directly)
    var publicDevTunnel = builder.AddDevTunnel("devtunnel-public")
        .WithAnonymousAccess()
        .WithReference(api.GetEndpoint("https"));

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
