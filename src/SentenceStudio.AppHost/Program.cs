using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var webapp = builder.AddProject<SentenceStudio_Web>("webapp");

var mauiapp = builder.AddProject<SentenceStudio>("mauiapp")
    .WithReference(webapp);

builder.Build().Run();
