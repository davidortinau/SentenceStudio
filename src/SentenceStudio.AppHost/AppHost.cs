using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var openaikey = builder.AddParameter("openaikey", secret: true);
var syncfusionkey = builder.AddParameter("syncfusionkey", secret: true);
var elevenlabskey = builder.AddParameter("elevenlabskey", secret: true);

var webapi = builder.AddProject<SentenceStudio_Web>("web");

var mauiapp = builder.AddMauiProject("mauiapp", "../SentenceStudio/SentenceStudio.csproj");

mauiapp.AddMacCatalystDevice()
    .WithEnvironment("SyncfusionKey", syncfusionkey)
    .WithEnvironment("AI__OpenAI__ApiKey", openaikey)
    .WithEnvironment("ElevenLabsKey", elevenlabskey)
    .WithReference(webapi);

mauiapp.AddWindowsDevice()
    .WithEnvironment("SyncfusionKey", syncfusionkey)
    .WithEnvironment("AI__OpenAI__ApiKey", openaikey)
    .WithEnvironment("ElevenLabsKey", elevenlabskey)
    .WithReference(webapi);

builder.Build().Run();
