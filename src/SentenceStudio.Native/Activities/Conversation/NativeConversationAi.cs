using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Scriban;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native;

/// <summary>
/// Source ConversationService.StartConversation / ContinueConversation,
/// rendered from the same Scriban templates with the same models and sent
/// through the owner-bound gateway (default Fast tier, as AiService.SendPrompt).
/// Unlike the source, failures are not swallowed into an empty reply: they
/// surface to the presenter, which shows the source error toast.
/// </summary>
public sealed class NativeConversationAi(IServiceProvider services) : INativeConversationAi
{
    /// <summary>Source ConversationService.DefaultPersonaName.</summary>
    public const string DefaultPersonaName = "김철수";

    private const string Prefix = "SentenceStudio.Native.Prompts.";

    public async Task<string?> StartAsync(string owner, ConversationScenario? scenario,
        string targetLanguage, CancellationToken cancellationToken)
    {
        var gateway = Gateway();
        string systemPrompt;
        string userPrompt;
        if (scenario is not null)
        {
            systemPrompt = await RenderAsync("Conversation.scenario.scriban-txt", new
            {
                scenario = new
                {
                    persona_name = scenario.PersonaName,
                    persona_description = scenario.PersonaDescription,
                    situation_description = scenario.SituationDescription,
                    conversation_type = scenario.ConversationType.ToString(),
                    question_bank = scenario.QuestionBank
                },
                target_language = targetLanguage
            }, cancellationToken);
            userPrompt = await RenderAsync("StartConversation.scenario.scriban-txt", new
            {
                scenario = new
                {
                    persona_name = scenario.PersonaName,
                    persona_description = scenario.PersonaDescription,
                    situation_description = scenario.SituationDescription,
                    conversation_type = scenario.ConversationType.ToString()
                },
                target_language = targetLanguage
            }, cancellationToken);
        }
        else
        {
            // Source GetSystemPromptAsync renders only the persona name.
            systemPrompt = await RenderAsync("Conversation.system.scriban-txt",
                new { name = DefaultPersonaName }, cancellationToken);
            userPrompt = await RenderAsync("StartConversation.scriban-txt",
                new { target_language = targetLanguage }, cancellationToken);
        }
        // Source sends "SYSTEM INSTRUCTIONS:\n{system}\n\nTASK:\n{task}" as one user
        // message; Azure's prompt shield rejects that (content_filter 400, observed
        // 2026-11-01 for every scenario and language). The same two texts go as
        // system instructions plus the task message instead.
        return await gateway.SendWithInstructionsAsync<string>(owner, systemPrompt, userPrompt,
            cancellationToken);
    }

    public async Task<Reply?> ContinueAsync(string owner, IReadOnlyList<ConversationChunk> chunks,
        ConversationScenario? scenario, string targetLanguage, CancellationToken cancellationToken)
    {
        var gateway = Gateway();
        var history = chunks.Select(chunk => new { author = chunk.Author, text = chunk.Text }).ToList();
        var prompt = scenario is not null
            ? await RenderAsync("ContinueConversation.scenario.scriban-txt", new
            {
                scenario = new
                {
                    persona_name = scenario.PersonaName,
                    persona_description = scenario.PersonaDescription,
                    situation_description = scenario.SituationDescription,
                    conversation_type = scenario.ConversationType.ToString()
                    // Source leaves question_bank out of the continue prompt.
                },
                chunks = history,
                target_language = targetLanguage
            }, cancellationToken)
            : await RenderAsync("ContinueConversation.scriban-txt", new
            {
                name = DefaultPersonaName,
                chunks = history,
                target_language = targetLanguage
            }, cancellationToken);
        return await gateway.SendPromptAsync<Reply>(owner, prompt, cancellationToken);
    }

    private IOwnerBoundNativeAiGateway Gateway() =>
        services.GetService<IOwnerBoundNativeAiGateway>()
            ?? throw new InvalidOperationException("Conversation AI is not available on this host.");

    internal static async Task<string> RenderAsync(string name, object model,
        CancellationToken cancellationToken)
    {
        var resource = Prefix + name;
        using var stream = typeof(NativeConversationAi).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"The source prompt {resource} is missing.");
        using var reader = new StreamReader(stream);
        var template = Template.Parse(await reader.ReadToEndAsync(cancellationToken));
        if (template.HasErrors)
            throw new InvalidOperationException($"The source prompt {resource} is invalid.");
        return await template.RenderAsync(model);
    }
}

/// <summary>
/// Scenarios live in the owner's device database (ConversationScenario is a
/// local, unsynced table). The MAUI heads seed the predefined list at startup
/// (SentenceStudioAppBuilder); native seeds the same list through the source
/// ScenarioService on first use. The repository gets a provider without
/// ISyncService so seeding does not start the unobserved fire-and-forget sync
/// the native heads avoid; no synced or owner-attributed row is written.
/// </summary>
public sealed class NativeConversationScenarios(
    IServiceProvider services,
    NativeActivityOwner owner,
    ILoggerFactory loggers) : INativeConversationScenarios
{
    public async Task<IReadOnlyList<ConversationScenario>> GetAllAsync(
        string userId, CancellationToken cancellationToken)
    {
        if (!owner.IsActive(userId))
            return [];
        var repository = new ScenarioRepository(new WithoutSync(services),
            loggers.CreateLogger<ScenarioRepository>());
        var scenarios = await repository.GetAllAsync();
        if (scenarios.Any(scenario => scenario.IsPredefined))
            return scenarios;
        cancellationToken.ThrowIfCancellationRequested();
        if (!owner.IsActive(userId))
            return [];
        await new ScenarioService(repository, loggers.CreateLogger<ScenarioService>())
            .SeedPredefinedScenariosAsync();
        return owner.IsActive(userId) ? await repository.GetAllAsync() : [];
    }

    private sealed class WithoutSync(IServiceProvider inner) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(ISyncService) ? null : inner.GetService(serviceType);
    }
}
