
using System.Text;
using CoreSync;
using CoreSync.Http.Server;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using SentenceStudio.Data;
using SentenceStudio.Web;
using SentenceStudio.Web.Auth;

var dbFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "sentencestudio", "server");
Directory.CreateDirectory(dbFolder);

string databasePath = Path.Combine(dbFolder, "sentencestudio.db");

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// --- Authentication: JWT Bearer (Identity-issued tokens) ---
var jwtSigningKey = builder.Configuration["Jwt:SigningKey"];
if (!string.IsNullOrWhiteSpace(jwtSigningKey))
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "SentenceStudio",
                ValidateAudience = true,
                ValidAudience = builder.Configuration["Jwt:Audience"] ?? "SentenceStudio.Api",
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(jwtSigningKey)),
                ValidateLifetime = true
            };
        });
}
else
{
    builder.Services.AddAuthentication(DevAuthHandler.SchemeName)
        .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, _ => { });
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