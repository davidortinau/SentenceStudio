using System.Security.Claims;
using System.Reflection;
using System.Text.Json;
using ElevenLabs;
using ElevenLabs.Models;
using ElevenLabs.TextToSpeech;
using ElevenLabs.Voices;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.AI;
using OpenAI;
using SentenceStudio.Api.Auth;
using SentenceStudio.Contracts.Ai;
using SentenceStudio.Contracts.Auth;
using SentenceStudio.Contracts.Plans;
using SentenceStudio.Contracts.Speech;
using SentenceStudio.Domain.Abstractions;
using SentenceStudio.Shared.Models;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddAuthentication(DevAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddScoped<ITenantContext, TenantContext>();

var openAiApiKey = builder.Configuration["AI__OpenAI__ApiKey"];
if (!string.IsNullOrWhiteSpace(openAiApiKey))
{
    builder.Services.AddSingleton<IChatClient>(_ =>
        new OpenAIClient(openAiApiKey).GetChatClient("gpt-4o-mini").AsIChatClient());
}

var elevenLabsKey = builder.Configuration["ElevenLabsKey"];
if (!string.IsNullOrWhiteSpace(elevenLabsKey))
{
    builder.Services.AddSingleton(new ElevenLabsClient(elevenLabsKey));
}

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

app.MapPost("/api/v1/ai/chat", async (ChatRequest request, [FromServices] IChatClient? chatClient, CancellationToken cancellationToken) =>
    {
        if (chatClient == null)
        {
            return Results.Problem("OpenAI client is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var responseType = ResolveResponseType(request.ResponseType);
        if (!string.IsNullOrWhiteSpace(request.ResponseType) && responseType == null)
        {
            return Results.BadRequest($"Unsupported response type '{request.ResponseType}'.");
        }

        if (responseType == null || responseType == typeof(string))
        {
            var options = string.IsNullOrWhiteSpace(request.Scenario)
                ? null
                : new ChatOptions { Instructions = request.Scenario };

            var response = await chatClient.GetResponseAsync(
                new[]
                {
                    new ChatMessage(ChatRole.User, request.Message)
                },
                options,
                cancellationToken);

            return Results.Ok(new SentenceStudio.Contracts.Ai.ChatResponse
            {
                Response = response.Text ?? string.Empty,
                Language = null
            });
        }

        var typedResult = await GetTypedResponseAsync(
            chatClient,
            request.Message,
            request.Scenario,
            responseType,
            cancellationToken);

        var json = JsonSerializer.Serialize(typedResult, responseType);
        return Results.Ok(new SentenceStudio.Contracts.Ai.ChatResponse
        {
            Response = json,
            Language = null
        });
    })
    .RequireAuthorization();

app.MapPost("/api/v1/speech/synthesize", async (SynthesizeRequest request, [FromServices] ElevenLabsClient? client) =>
    {
        if (client == null)
        {
            return Results.Problem("ElevenLabs client is not configured.", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var voiceId = string.IsNullOrWhiteSpace(request.VoiceId)
            ? "21m00Tcm4TlvDq8ikWAM"
            : request.VoiceId;

        var voice = await client.VoicesEndpoint.GetVoiceAsync(voiceId);
        var ttsRequest = new TextToSpeechRequest(
            voice,
            request.Text,
            model: Model.MultiLingualV2);

        var audioBytes = await client.TextToSpeechEndpoint.TextToSpeechAsync(ttsRequest);
        var base64 = Convert.ToBase64String(audioBytes.ClipData.ToArray());

        return Results.Ok(new SynthesizeResponse
        {
            AudioUrl = $"data:audio/mpeg;base64,{base64}"
        });
    })
    .RequireAuthorization();

app.MapPost("/api/v1/plans/generate", (GeneratePlanRequest request) =>
        Results.Ok(BuildPlanResponse(request)))
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

static GeneratePlanResponse BuildPlanResponse(GeneratePlanRequest request)
{
    var totalMinutes = Math.Clamp(request.Minutes ?? 30, 10, 90);
    var vocabMinutes = Math.Clamp(totalMinutes / 3, 6, 12);
    var conversationMinutes = Math.Clamp(totalMinutes / 3, 5, 15);
    var remainingMinutes = totalMinutes - vocabMinutes - conversationMinutes;

    if (remainingMinutes < 5)
    {
        conversationMinutes += remainingMinutes;
        remainingMinutes = 0;
    }

    var activities = new List<GeneratePlanActivity>
    {
        new()
        {
            ActivityType = "VocabularyReview",
            EstimatedMinutes = vocabMinutes,
            Priority = 1,
            VocabWordCount = Math.Clamp(vocabMinutes * 2, 8, 24)
        },
        new()
        {
            ActivityType = "Conversation",
            EstimatedMinutes = conversationMinutes,
            Priority = 2
        }
    };

    if (remainingMinutes > 0)
    {
        activities.Add(new GeneratePlanActivity
        {
            ActivityType = "Reading",
            EstimatedMinutes = remainingMinutes,
            Priority = 3
        });
    }

    return new GeneratePlanResponse
    {
        PlanId = Guid.NewGuid().ToString("N")[..16],
        Rationale = "Locally generated API plan balancing review and production activities.",
        Activities = activities
    };
}

static async Task<object?> GetTypedResponseAsync(
    IChatClient chatClient,
    string message,
    string? instructions,
    Type responseType,
    CancellationToken cancellationToken)
{
    var method = typeof(Program)
        .GetMethod(nameof(GetTypedResponseCoreAsync), BindingFlags.NonPublic | BindingFlags.Static)!
        .MakeGenericMethod(responseType);

    var task = (Task<object?>)method.Invoke(null, new object?[] { chatClient, message, instructions, cancellationToken })!;
    return await task;
}

static async Task<object?> GetTypedResponseCoreAsync<T>(
    IChatClient chatClient,
    string message,
    string? instructions,
    CancellationToken cancellationToken)
{
    var options = string.IsNullOrWhiteSpace(instructions)
        ? null
        : new ChatOptions { Instructions = instructions };

    var response = await chatClient.GetResponseAsync<T>(
        new[]
        {
            new ChatMessage(ChatRole.User, message)
        },
        options,
        cancellationToken: cancellationToken);
    return response.Result;
}

static Type? ResolveResponseType(string? responseType)
{
    if (string.IsNullOrWhiteSpace(responseType))
    {
        return null;
    }

    if (responseType == typeof(string).FullName || responseType == typeof(string).AssemblyQualifiedName)
    {
        return typeof(string);
    }

    var normalized = responseType.Split(',')[0].Trim();
    return Type.GetType(responseType, throwOnError: false)
        ?? Type.GetType(normalized, throwOnError: false)
        ?? AppDomain.CurrentDomain
            .GetAssemblies()
            .Select(assembly => assembly.GetType(normalized, throwOnError: false))
            .FirstOrDefault(type => type != null);
}

app.Run();

record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
