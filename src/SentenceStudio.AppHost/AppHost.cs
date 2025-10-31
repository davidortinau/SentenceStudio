using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var webapi = builder.AddProject<SentenceStudio_Web>("web");

var mauiapp = builder.AddMauiProject("mauiapp", "../SentenceStudio/SentenceStudio.csproj");

mauiapp.AddMacCatalystDevice()
    .WithReference(webapi);

builder.Build().Run();
