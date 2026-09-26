using System.ComponentModel;
using Microsoft.Extensions.AI;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Services;

namespace SentenceStudio.Api.Coach.Application;

public interface ICoachVocabularySetGenerator
{
    Task<CoachVocabularySetProposal?> PrepareAsync(
        string topic,
        string targetLanguageTag,
        string nativeLanguageTag,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Uses the configured OpenAI fast-tier model to prepare a bounded topical set. It receives no
/// saved vocabulary, review state, plan data, or learner identity.
/// </summary>
public sealed class CoachVocabularySetGenerator(
    IServiceProvider services,
    ILogger<CoachVocabularySetGenerator> logger) : ICoachVocabularySetGenerator
{
    public async Task<CoachVocabularySetProposal?> PrepareAsync(
        string topic,
        string targetLanguageTag,
        string nativeLanguageTag,
        CancellationToken cancellationToken = default)
    {
        var client = services.GetKeyedService<IChatClient>(AiTier.Fast.ToKey());
        if (client is null)
        {
            logger.LogWarning("Coach vocabulary set could not be prepared because the OpenAI client is unavailable");
            return null;
        }

        ChatResponse<GeneratedVocabularySet> response;
        try
        {
            response = await client.GetResponseAsync<GeneratedVocabularySet>(
                [
                    new ChatMessage(
                        ChatRole.User,
                        $"Prepare vocabulary about this topic: {topic}")
                ],
                new ChatOptions
                {
                    Instructions = $"""
                        You prepare concise topical vocabulary for language study.
                        The target language tag is {targetLanguageTag}.
                        The native language tag is {nativeLanguageTag}.
                        Return exactly ten unique, common target-language words or short phrases with
                        concise native-language meanings. Stay strictly on the requested topic. Do not
                        include identifiers, notes, examples, markdown, or saved learner data.
                        """
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Coach vocabulary set generation failed for topic length {TopicLength}",
                topic.Length);
            return null;
        }

        var items = response.Result?.Items ?? [];
        if (items.Count != 10
            || items.Any(item => string.IsNullOrWhiteSpace(item.TargetText)
                                 || string.IsNullOrWhiteSpace(item.NativeMeaning))
            || items.Select(item => item.TargetText.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 10)
        {
            logger.LogWarning(
                "Coach vocabulary set generation returned an invalid item shape for topic {TopicLength}",
                topic.Length);
            return null;
        }

        return new CoachVocabularySetProposal
        {
            ProposalId = Guid.NewGuid().ToString("N"),
            Topic = topic,
            Title = $"{topic} vocabulary",
            TargetLanguageTag = targetLanguageTag,
            Terms = items.Select(item => new CoachVocabularyTermDto
            {
                TargetTerm = item.TargetText.Trim(),
                NativeTerm = item.NativeMeaning.Trim()
            }).ToArray()
        };
    }

    private sealed class GeneratedVocabularySet
    {
        [Description("Exactly ten unique topical vocabulary items.")]
        public List<GeneratedVocabularyItem> Items { get; set; } = [];
    }

    private sealed class GeneratedVocabularyItem
    {
        [Description("A word or short phrase in the target language.")]
        public string TargetText { get; set; } = string.Empty;

        [Description("Its concise meaning in the native language.")]
        public string NativeMeaning { get; set; } = string.Empty;
    }
}
