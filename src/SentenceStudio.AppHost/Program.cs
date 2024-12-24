using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var postgres = 
    builder.AddPostgres("postgres")
        .WithDataVolume(isReadOnly: false)
        .WithPgAdmin();

var postgresdb = 
    postgres.AddDatabase("SentenceStudioDB");

builder.AddProject<SentenceStudio_WebAPI>("SentenceStudioWebAPI")
    .WithReference(postgresdb);

builder.Build().Run();
