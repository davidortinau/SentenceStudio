
using CoreSync;
using CoreSync.Http.Server;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Identity.Web;
using SentenceStudio.Data;
using SentenceStudio.Web;
using SentenceStudio.Web.Auth;

var dbFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sentencestudio", "server");
Directory.CreateDirectory(dbFolder);

string databasePath = Path.Combine(dbFolder, "sentencestudio.db");

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// --- Authentication (same pattern as SentenceStudio.Api) ---
var useEntraId = builder.Configuration.GetValue<bool>("Auth:UseEntraId");

if (useEntraId)
{
    builder.Services.AddAuthentication(Microsoft.Identity.Web.Constants.Bearer)
        .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));

    builder.Services.AddAuthorization(options =>
    {
        options.AddPolicy("RequireSyncReadWrite", policy =>
            policy.RequireScope("sync.readwrite"));
    });
}
else
{
    builder.Services.AddAuthentication(DevAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, _ => { });
    builder.Services.AddAuthorization();
}

builder.Services.AddDataServices(databasePath);
builder.Services.AddSyncServices(databasePath);

// Add services to the container.
builder.Services.AddOpenApi();

var app = builder.Build();

// Authentication runs before CoreSync middleware so the user identity
// is available to downstream handlers. In dev mode the DevAuthHandler
// creates a synthetic identity; in production, Bearer tokens are validated.
app.UseAuthentication();
app.UseAuthorization();

app.UseCoreSyncHttpServer();

app.UseLatestDatabaseVersion();

app.SetupServerSynchronization();

app.Run();