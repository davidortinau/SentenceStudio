
using CoreSync.Http.Server;
using SentenceStudio.Data;
using SentenceStudio.Web;

var dbFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sentencestudio", "server");
Directory.CreateDirectory(dbFolder);

string databasePath = Path.Combine(dbFolder, "sentencestudio.db");

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Configure HTTPS for development
// builder.WebHost.ConfigureKestrel(options =>
// {
//     options.ListenLocalhost(5241, listenOptions =>
//     {
//         listenOptions.UseHttps();
//     });
// });

builder.Services.AddDataServices(databasePath);
builder.Services.AddSyncServices(databasePath);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();


var app = builder.Build();

// Configure the HTTP request pipeline.
// if (app.Environment.IsDevelopment())
// {
//     app.MapOpenApi();
// }

app.UseCoreSyncHttpServer();

app.UseLatestDatabaseVersion();

app.SetupServerSynchronization();

// app.UseHttpsRedirection();

app.Run();