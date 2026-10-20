using System.Collections.Immutable;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native;

public sealed partial class NativeQuizPresenter
{
    private IReadOnlyDictionary<string, IReadOnlyList<VocabQuizSentenceHint>> _sentenceHints =
        new Dictionary<string, IReadOnlyList<VocabQuizSentenceHint>>(StringComparer.Ordinal);
    private bool _sentenceHintsExpanded;
    private NativeQuizPhotoAsset? _photoAsset;
    private bool _photoDesired;
    private bool _answerAudioAvailable;
    private readonly HashSet<int> _heardAnswerChoices = [];
    private DateTimeOffset? _feedbackStartedAt;

    private async Task LoadSentenceHintsAsync(CancellationToken cancellationToken)
    {
        _sentenceHints = new Dictionary<string, IReadOnlyList<VocabQuizSentenceHint>>(
            StringComparer.Ordinal);
        _sentenceHintsExpanded = false;
        if (_launch is null || _profile is null || !HasOwner(_launch.UserId)
            || _batch.Count == 0)
            return;
        try
        {
            using var hintScope = scopes.CreateScope();
            var hints = await hintScope.ServiceProvider
                .GetRequiredService<ExampleSentenceRepository>()
                .GetQuizHintsForWordsAsync(
                    _launch.UserId, _batch.Select(item => item.Word.Id),
                    _profile.TargetCEFRLevel,
                    VocabQuizSentenceHintPolicy.MaxHintsPerTurn,
                    cancellationToken);
            if (!HasOwner(_launch.UserId))
                return;
            _sentenceHints = hints.GroupBy(item => item.VocabularyWordId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key,
                    group => (IReadOnlyList<VocabQuizSentenceHint>)group
                        .Take(VocabQuizSentenceHintPolicy.MaxHintsPerTurn).ToArray(),
                    StringComparer.Ordinal);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Owner-scoped quiz sentence examples are unavailable.");
        }
    }

    private ImmutableArray<NativeQuizSentenceHint> CurrentSentenceHints()
    {
        if (_turnIndex >= _round.Count || _launch is null || !HasOwner(_launch.UserId))
            return [];
        _sentenceHints.TryGetValue(_round[_turnIndex].Word.Id, out var available);
        return VocabQuizSentenceHintPolicy.GetHintsForTurn(_promptUsesNative, available)
            .Select(item => new NativeQuizSentenceHint(
                item.ExampleSentenceId, item.TargetSentence))
            .ToImmutableArray();
    }

    public NativeQuizState ToggleSentenceHints()
    {
        if (_launch is null || !HasOwner(_launch.UserId)
            || State.Phase is not (NativePhase.Ready or NativePhase.Feedback)
            || State.Overlay != NativeQuizOverlay.None || State.Sentence is not null)
            return RefuseInvalidAction();
        var available = CurrentSentenceHints();
        if (available.IsDefaultOrEmpty)
            return RefuseInvalidAction();
        _sentenceHintsExpanded = VocabQuizSentenceHintPolicy.GetExpandedState(
            _sentenceHintsExpanded, isEligible: true,
            VocabQuizSentenceHintTransition.Toggle);
        return SetState(State with
        {
            SentenceHintsExpanded = _sentenceHintsExpanded,
            SentenceHints = _sentenceHintsExpanded ? available : []
        });
    }

    private void ResetSentenceHints(VocabQuizSentenceHintTransition transition)
    {
        _sentenceHintsExpanded = VocabQuizSentenceHintPolicy.GetExpandedState(
            _sentenceHintsExpanded,
            !CurrentSentenceHints().IsDefaultOrEmpty,
            transition);
    }
}
