using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using SentenceStudio.Api.Auth;
using SentenceStudio.Contracts.Ai;
using SentenceStudio.Contracts.Auth;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Contracts.Speech;
using SentenceStudio.Domain.Abstractions;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddAuthentication(DevAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddScoped<ITenantContext, TenantContext>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantContextMiddleware>();

app.MapGet("/api/v1/auth/bootstrap", (ClaimsPrincipal user, ITenantContext tenantContext) =>
    Results.Ok(new BootstrapResponse
    {
        TenantId = tenantContext.TenantId ?? user.FindFirstValue("tenant_id"),
        UserId = tenantContext.UserId ?? user.FindFirstValue(ClaimTypes.NameIdentifier),
        DisplayName = tenantContext.DisplayName ?? user.FindFirstValue(ClaimTypes.Name),
        Email = tenantContext.Email ?? user.FindFirstValue(ClaimTypes.Email)
    }))
    .RequireAuthorization();

app.MapPost("/api/v1/ai/chat", (ChatRequest request) =>
        Results.Problem("AI chat endpoint not implemented yet.", statusCode: StatusCodes.Status501NotImplemented))
    .RequireAuthorization();

app.MapPost("/api/v1/speech/synthesize", (SynthesizeRequest request) =>
        Results.Problem("Speech synthesis endpoint not implemented yet.", statusCode: StatusCodes.Status501NotImplemented))
    .RequireAuthorization();

app.MapPost("/api/v1/plans/generate", (GeneratePlanRequest request) =>
        Results.Problem("Plan generation endpoint not implemented yet.", statusCode: StatusCodes.Status501NotImplemented))
    .RequireAuthorization();

var summaries = new[]
{
    "Freezing", "Bracing", "Chilly", "Cool", "Mild", "Warm", "Balmy", "Hot", "Sweltering", "Scorching"
};

app.MapGet("/weatherforecast", () =>
{
    var forecast =  Enumerable.Range(1, 5).Select(index =>
        new WeatherForecast
        (
            DateOnly.FromDateTime(DateTime.Now.AddDays(index)),
            Random.Shared.Next(-20, 55),
            summaries[Random.Shared.Next(summaries.Length)]
        ))
        .ToArray();
    return forecast;
})
.WithName("GetWeatherForecast");

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
