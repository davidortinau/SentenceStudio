using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using System.Globalization;
using Scriban;
using SentenceStudio;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Services.Api;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native.Adapters;

public interface IOwnerBoundNativeSentenceGateway
{
    Task<GradeResponse?> GradeAsync(
        string userId, string renderedSourcePrompt, CancellationToken cancellationToken);
}

/// <summary>Uses the same source GradeSentence template, reasoning tier and response DTO.</summary>
public sealed class NativeQuizSentenceGrader(
    IOwnerBoundNativeSentenceGateway gateway,
    IUserScopeProvider scope,
    IPreferencesService preferences,
    LearningResourceRepository resources) : INativeQuizSentenceGrader
{
    private const string TemplateResource =
        "SentenceStudio.Native.Prompts.GradeSentence.scriban-txt";

    public async Task<NativeQuizSentenceGrade> GradeAsync(
        string userId, string sentence, VocabularyWord word,
        string targetLanguage, string nativeLanguage, CancellationToken cancellationToken)
    {
        NativeQuizOwnerGuard.RequireActiveOwner(
            userId, scope, preferences, cancellationToken);
        if (string.IsNullOrWhiteSpace(sentence)
            || !FocusVocabularySelection.IsUsableVocabularyWord(word))
            throw new ArgumentException("Sentence grading needs a target-language sentence and word.");
        await NativeQuizOwnerGuard.RequireOwnerReachableWordsAsync(
            userId, [word], scope, preferences, resources, cancellationToken);

        using var stream = typeof(NativeQuizSentenceGrader).Assembly
            .GetManifestResourceStream(TemplateResource)
            ?? throw new InvalidOperationException("The source sentence grading template is missing.");
        using var reader = new StreamReader(stream);
        var template = Template.Parse(await reader.ReadToEndAsync(cancellationToken));
        if (template.HasErrors)
            throw new InvalidOperationException("The source sentence grading template is invalid.");
        var prompt = await template.RenderAsync(new
        {
            user_input = sentence,
            user_meaning = string.Empty,
            native_language = nativeLanguage,
            target_language = targetLanguage,
            target_word = word.TargetLanguageTerm,
            target_word_meaning = word.NativeLanguageTerm
        });
        var grade = await gateway.GradeAsync(userId, prompt, cancellationToken)
            ?? throw new InvalidOperationException("The sentence grader returned no verdict.");
        NativeQuizOwnerGuard.RequireActiveOwner(
            userId, scope, preferences, cancellationToken);
        var analysis = VocabQuizSentenceUsagePolicy.GetTargetWordAnalysis(grade, word);
        var formVerified = analysis is not null
            && VocabQuizSentenceUsagePolicy.HasVerifiedTargetUse(
                sentence, word, targetLanguage, analysis.UsedForm);
        var correct = formVerified && analysis!.UsageCorrect;
        var explanation = formVerified ? analysis!.UsageExplanation : null;
        if (string.IsNullOrWhiteSpace(explanation))
        {
            var key = !formVerified
                ? NativeText.SentenceTargetUnconfirmed
                : correct ? NativeText.SentenceMeaningful : NativeText.SentenceUnclear;
            explanation = string.Format(CultureInfo.CurrentCulture,
                LocalizationManager.Instance.GetString(key, CultureInfo.CurrentUICulture),
                word.TargetLanguageTerm);
        }
        var verifiedGrade = formVerified ? grade : null;
        return new NativeQuizSentenceGrade(
            sentence, correct, explanation,
            verifiedGrade?.Accuracy, verifiedGrade?.Fluency,
            verifiedGrade?.AccuracyExplanation ?? string.Empty,
            verifiedGrade?.FluencyExplanation ?? string.Empty,
            verifiedGrade?.GrammarNotes?.RecommendedTranslation ?? string.Empty,
            verifiedGrade?.GrammarNotes?.Explanation ?? string.Empty)
        {
            TargetUsedForm = formVerified ? analysis!.UsedForm : null,
            HasVerifiedTargetUse = formVerified
        };
    }
}

public sealed class OwnerBoundNativeSentenceGateway(
    IAuthService auth,
    IPreferencesService preferences,
    DeviceUserScopeProvider scope,
    IHttpMessageHandlerFactory handlers,
    Uri apiRoot) : IOwnerBoundNativeSentenceGateway
{
    public async Task<GradeResponse?> GradeAsync(
        string owner, string renderedSourcePrompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stamp = scope.CaptureStamp();
        bool HasOwner() =>
            !string.IsNullOrWhiteSpace(owner)
            && stamp.PublishedOwner == owner && scope.IsCurrent(stamp)
            && scope.TryGetUserProfileId(out var active) && active == owner
            && preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty) == owner
            && auth.IsSignedIn;
        if (!HasOwner())
            throw new UnauthorizedAccessException(
                "Sentence grading requires an authenticated active owner.");

        var bearer = new OwnerBoundNativeAiBearerHandler(auth, apiRoot, owner, HasOwner)
        {
            InnerHandler = handlers.CreateHandler(OwnerBoundNativeAiGateway.ClientName)
        };
        // The handler factory owns its pooled inner pipeline.
        using var client = new HttpClient(bearer, disposeHandler: false)
        {
            BaseAddress = apiRoot,
            Timeout = TimeSpan.FromSeconds(45)
        };
        // Existing AiGatewayClient includes response fragments in its error logger.
        // A learner's sentence can appear in that response, so do not log it here.
        var gateway = new AiGatewayClient(
            new AiApiClient(client), NullLogger<AiGatewayClient>.Instance);
        var response = await gateway.SendPromptAsync<GradeResponse>(
            renderedSourcePrompt, AiTier.Reasoning,
            reasoningEffort: "minimal", cancellationToken: cancellationToken);
        if (!HasOwner())
            throw new UnauthorizedAccessException(
                "Sentence grading owner changed before the result arrived.");
        return response;
    }
}

public static class NativeQuizSentenceGraderRegistration
{
    public static IServiceCollection AddOwnerBoundNativeSentenceGrader(
        this IServiceCollection services, Uri apiRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (!apiRoot.IsAbsoluteUri || apiRoot.Scheme is not ("http" or "https"))
            throw new ArgumentException("Sentence grading needs the configured API origin.",
                nameof(apiRoot));

        services.TryAddSingleton<IOwnerBoundNativeSentenceGateway>(sp =>
            new OwnerBoundNativeSentenceGateway(
                sp.GetRequiredService<IAuthService>(),
                sp.GetRequiredService<IPreferencesService>(),
                sp.GetRequiredService<DeviceUserScopeProvider>(),
                sp.GetRequiredService<IHttpMessageHandlerFactory>(),
                apiRoot));
        services.Replace(ServiceDescriptor.Singleton<INativeQuizSentenceGrader,
            NativeQuizSentenceGrader>());
        return services;
    }
}
