using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var webapi = builder.AddProject<SentenceStudio_Web>("web");

builder.AddProject<SentenceStudio>("app")
    .WithReference(webapi)
    .ExcludeFromManifest();

builder.Build().Run();
