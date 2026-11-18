using SentenceStudio.Contracts.Coach;
using SentenceStudio.Native.Features.Skills;
using SentenceStudio.Native.Features.Vocabulary;

namespace SentenceStudio.Native;

/// <summary>
/// What one screen shows, in the terms Sam's tools understand: the things on it (with the ids the
/// tools take), whether it is keeping an answer from the learner, and short state.
/// </summary>
public sealed record NativeSamScreenSnapshot(
    IReadOnlyList<CoachClientScreenItem> Items,
    bool AnswerHidden = false,
    IReadOnlyList<(string Key, string? Value)>? Facts = null)
{
    public static NativeSamScreenSnapshot Empty { get; } = new([]);
}

/// <summary>
/// Describes the presenter behind the screen the learner is on, so Sam can resolve "this". One
/// place for every native head: iOS and Android hand it the same presenters and send the same
/// description.
/// </summary>
/// <remarks>
/// Only what the screen shows goes in. A quiz answer, a translation's recommended sentence or a
/// cloze word the learner has not reached yet stays out, and <see cref="NativeSamScreenSnapshot.AnswerHidden"/>
/// tells Sam not to give it away.
/// </remarks>
public static class NativeSamScreen
{
    private const string Vocab = CoachClientScreenItemKinds.VocabularyWord;
    private const string Text = CoachClientScreenItemKinds.Text;

    /// <summary>
    /// The description for <paramref name="source"/>, or empty when it describes nothing. An item
    /// with neither an id nor a label (an empty draft) points at nothing, so it is left out.
    /// </summary>
    public static NativeSamScreenSnapshot Describe(object? source)
    {
        var shown = DescribeSource(source);
        return shown.Items.All(Names) ? shown : shown with { Items = [.. shown.Items.Where(Names)] };
    }

    private static bool Names(CoachClientScreenItem item) => item.Id is not null || item.Label is not null;

    /// <summary>
    /// Adds the open activity and what it was launched with (its resources and skill), after what
    /// the activity shows, so "this activity" and "this resource" have something to point at.
    /// </summary>
    public static NativeSamScreenSnapshot WithLaunch(
        NativeSamScreenSnapshot shown, NativeActivityLaunch launch, string? title)
    {
        var items = shown.Items.ToList();
        var focus = items.Any(item => item.Focus);
        foreach (var resource in launch.ResourceIds.IsDefault ? [] : launch.ResourceIds.Take(5))
        {
            if (items.Any(item => item.Kind == CoachClientScreenItemKinds.LearningResource && item.Id == resource))
                continue;
            // A resource the activity shows without an id (Reading's title) gets the launch's id.
            var unnamed = items.FindIndex(item => item.Kind == CoachClientScreenItemKinds.LearningResource && item.Id is null);
            if (unnamed >= 0 && launch.ResourceIds.Length == 1)
            {
                var known = items[unnamed];
                items[unnamed] = new CoachClientScreenItem
                {
                    Kind = known.Kind, Id = resource, Label = known.Label, Detail = known.Detail, Focus = known.Focus
                };
                continue;
            }
            items.Add(Item(CoachClientScreenItemKinds.LearningResource, resource, null));
        }
        if (!string.IsNullOrWhiteSpace(launch.SkillId))
            items.Add(Item(CoachClientScreenItemKinds.Skill, launch.SkillId, null));
        items.Add(Item(CoachClientScreenItemKinds.Activity, launch.Route, title, focus: !focus));
        return shown with { Items = items };
    }

    private static NativeSamScreenSnapshot DescribeSource(object? source) => source switch
    {
        NativeQuizPresenter quiz => Quiz(quiz.State),
        NativeClozePresenter cloze => Cloze(cloze.State),
        NativeFlashcardPresenter cards => Flashcards(cards.State),
        NativeTranslationPresenter translation => Translation(translation.State),
        NativeWritingPresenter writing => Writing(writing.State),
        NativeReadingPresenter reading => Reading(reading.State),
        NativeShadowingPresenter shadowing => Shadowing(shadowing.State),
        NativeVideoWatchingPresenter video => Video(video.State),
        NativeConversationPresenter conversation => Conversation(conversation.State),
        NativeScenePresenter scene => Scene(scene.State),
        NativeWordAssociationPresenter association => WordAssociation(association.State),
        NativeHowDoYouSayPresenter howDoYouSay => HowDoYouSay(howDoYouSay.State),
        NativeMatchingPresenter matching => Matching(matching.State),
        NativeMinimalPairsPresenter pairs => MinimalPairs(pairs.State),
        NativeNumberDrillPresenter numbers => NumberDrill(numbers.State),
        NativeDiaryPresenter diary => Diary(diary.State),
        NativeDashboardPresenter dashboard => Dashboard(dashboard.State),
        NativeResourcePresenter resources => Resource(resources.Detail),
        VocabularyBrowser vocabulary => VocabularyForm(vocabulary),
        SkillsCoordinator skills => SkillForm(skills),
        _ => NativeSamScreenSnapshot.Empty
    };

    internal static NativeSamScreenSnapshot Quiz(NativeQuizState state)
    {
        if (state.Sentence is { } sentence)
        {
            // Writing a sentence with a word the learner can see: nothing to hide.
            return new([Item(Vocab, sentence.WordId, sentence.TargetLanguageTerm, focus: true)],
                Facts: [("mode", "sentence"), ("phase", state.Phase.ToString())]);
        }

        // The resume choice covers the quiz, so a restored prompt is not on screen.
        if (state.Prompt is not { } prompt || state.Phase == NativePhase.ResumeChoice)
            return new([], Facts: [("phase", state.Phase.ToString())]);

        var graded = state.CorrectAnswerAfterGrading is not null || state.AnswerWasCorrect is not null;
        var label = prompt.ShowWrittenPrompt || prompt.ShowTextWithPhoto ? prompt.PromptText : null;
        return new(
            [Item(Vocab, prompt.WordId, label, graded ? state.CorrectAnswerAfterGrading : null, focus: true)],
            AnswerHidden: !graded && state.Phase == NativePhase.Ready,
            Facts:
            [
                ("mode", state.Mode),
                ("phase", state.Phase.ToString()),
                ("turn", $"{state.CurrentTurn}/{state.TurnsInRound}"),
                ("promptLanguage", prompt.IsTargetLanguagePrompt ? "target" : "native"),
                ("correctionRequired", state.RequiresCorrection ? "true" : null),
            ]);
    }

    private static NativeSamScreenSnapshot Cloze(NativeClozeState state) =>
        new([Item(Text, null, state.DisplaySentence, state.IsGraded ? state.CorrectAnswer : null, focus: true)],
            AnswerHidden: !state.IsGraded && state.Phase == NativePhase.Ready,
            Facts: [("translation", state.Translation), ("item", $"{state.Index + 1}/{state.Total}")]);

    private static NativeSamScreenSnapshot Flashcards(NativeFlashcardState state) =>
        state.Current is not { } card ? NativeSamScreenSnapshot.Empty
            : new([Item(Vocab, card.WordId, card.TargetTerm, state.IsFlipped ? card.NativeTerm : null, focus: true)],
                AnswerHidden: !state.IsFlipped,
                Facts: [("card", $"{state.CurrentIndex + 1}/{state.Total}")]);

    private static NativeSamScreenSnapshot Translation(NativeTranslationState state) =>
        new([Item(Text, null, state.Prompt, state.Feedback?.RecommendedTranslation, focus: true)],
            AnswerHidden: state.Feedback is null && state.Phase == NativePhase.Ready,
            Facts: [("draft", state.Input), ("item", $"{state.Index + 1}/{state.Count}")]);

    private static NativeSamScreenSnapshot Writing(NativeWritingState state) =>
        new([Item(Text, null, state.Draft, focus: true)],
            Facts: [("sentencesWritten", state.Sentences.IsDefaultOrEmpty ? "0" : state.Sentences.Length.ToString())]);

    private static NativeSamScreenSnapshot Reading(NativeReadingState state)
    {
        var items = new List<CoachClientScreenItem>();
        if (state.WordPanel is { } word)
            items.Add(Item(Vocab, null, word.Word, word.Definition, focus: true));
        if (!state.Sentences.IsDefaultOrEmpty && state.CurrentSentenceIndex >= 0
            && state.CurrentSentenceIndex < state.Sentences.Length)
            items.Add(Item(Text, null, state.Sentences[state.CurrentSentenceIndex].Text, focus: items.Count == 0));
        items.Add(Item(CoachClientScreenItemKinds.LearningResource, null, state.ResourceTitle, focus: items.Count == 0));
        return new(items);
    }

    private static NativeSamScreenSnapshot Shadowing(NativeShadowingState state) =>
        state.Sentences.IsDefaultOrEmpty || state.Index < 0 || state.Index >= state.Sentences.Length
            ? NativeSamScreenSnapshot.Empty
            : new([Item(Text, null, state.Sentences[state.Index].TargetText,
                state.ShowTranslation ? state.Sentences[state.Index].NativeText : null, focus: true)]);

    private static NativeSamScreenSnapshot Video(NativeVideoWatchingState state) =>
        new([Item(CoachClientScreenItemKinds.LearningResource, null, state.ResourceTitle, focus: true)],
            Facts: [("transcriptShown", state.ShowTranscript ? "true" : "false")]);

    private static NativeSamScreenSnapshot Conversation(NativeConversationState state)
    {
        var last = state.Messages.IsDefaultOrEmpty ? null : state.Messages[^1];
        return new(
            last is null ? [] : [Item(Text, null, last.Text, focus: true)],
            Facts: [("scenario", state.ActiveScenarioId), ("partner", state.PartnerName), ("draft", state.Draft)]);
    }

    private static NativeSamScreenSnapshot Scene(NativeSceneState state) =>
        new([Item(Text, null, state.Draft, focus: true)],
            Facts: [("descriptions", state.Results.IsDefaultOrEmpty ? "0" : state.Results.Length.ToString())]);

    private static NativeSamScreenSnapshot WordAssociation(NativeWordAssociationState state) =>
        new([Item(Vocab, null, state.TargetTerm, state.NativeTerm, focus: true)],
            Facts: [("draft", state.Draft), ("word", $"{state.Index + 1}/{state.WordCount}")]);

    private static NativeSamScreenSnapshot HowDoYouSay(NativeHowDoYouSayState state) =>
        new([Item(Text, null, state.Input, focus: true)]);

    private static NativeSamScreenSnapshot Matching(NativeMatchingState state) =>
        new(state.Tiles.IsDefaultOrEmpty ? [] : state.Tiles
                .Where(tile => tile.IsVisible && tile.IsTarget)
                .Take(8)
                .Select(tile => Item(Vocab, tile.WordId, tile.Text))
                .ToList(),
            AnswerHidden: state.Phase == NativePhase.Ready);

    private static NativeSamScreenSnapshot MinimalPairs(NativeMinimalPairsState state)
    {
        if (state.Session is { Left: { } left, Right: { } right } session)
        {
            return new(
            [
                Item(Vocab, left.WordId, left.Term),
                Item(Vocab, right.WordId, right.Term)
            ], AnswerHidden: !session.HasChecked);
        }

        var list = state.List;
        var selected = list?.SelectedPairId is { } id && !list.Pairs.IsDefaultOrEmpty
            ? list.Pairs.FirstOrDefault(pair => pair.Id == id) : null;
        return selected is null ? NativeSamScreenSnapshot.Empty
            : new([Item(CoachClientScreenItemKinds.MinimalPair, selected.Id,
                $"{selected.WordA} / {selected.WordB}", selected.ContrastLabel, focus: true)]);
    }

    private static NativeSamScreenSnapshot NumberDrill(NativeNumberDrillState state) =>
        state.Item is not { } item ? new([], Facts: [("stage", state.Stage.ToString())])
            : new([Item(Text, null, item.DisplayPrompt, focus: true)],
                AnswerHidden: state.Feedback is null,
                Facts: [("context", item.ContextCode), ("mode", item.SubModeCode), ("system", item.System)]);

    private static NativeSamScreenSnapshot Diary(NativeDiaryState state) =>
        state.Editor is { } editor
            ? new([Item(Text, null, editor.Content, editor.PromptText, focus: true)])
            : state.Viewer is { } viewer
                ? new([Item(Text, viewer.Id, viewer.Content, viewer.PromptText, focus: true)])
                : NativeSamScreenSnapshot.Empty;

    private static NativeSamScreenSnapshot Dashboard(NativeDashboardState state) =>
        new(state.Cards.IsDefaultOrEmpty ? [] : state.Cards
            .Take(8)
            .Select(card => new CoachClientScreenItem
            {
                Kind = CoachClientScreenItemKinds.PlanItem,
                Id = card.Id,
                Label = card.Title,
                Detail = card.ResourceTitle ?? (card.IsCompleted ? "completed" : null)
            })
            .ToList(),
            Facts: [("mode", state.Mode.ToString())]);

    private static NativeSamScreenSnapshot Resource(NativeResourceDetailState detail) =>
        detail.Resource is not { } resource ? NativeSamScreenSnapshot.Empty
            : new([Item(CoachClientScreenItemKinds.LearningResource, resource.Id, resource.Title,
                resource.MediaType, focus: true)],
                Facts: [("words", resource.VocabularyCount.ToString())]);

    private static NativeSamScreenSnapshot VocabularyForm(VocabularyBrowser browser) =>
        browser.FormDetail is not { } detail ? NativeSamScreenSnapshot.Empty
            : new([Item(Vocab, detail.Word.Id, detail.Word.TargetTerm, detail.Word.NativeTerm, focus: true)],
                Facts: [("status", detail.Word.Status.ToString())]);

    private static NativeSamScreenSnapshot SkillForm(SkillsCoordinator skills) =>
        !skills.IsFormOpen ? NativeSamScreenSnapshot.Empty
            : new([Item(CoachClientScreenItemKinds.Skill, skills.FormSkillId, skills.FormState.Title,
                skills.FormState.Description, focus: true)]);

    private static CoachClientScreenItem Item(
        string kind, string? id, string? label, string? detail = null, bool focus = false) => new()
    {
        Kind = kind,
        Id = string.IsNullOrWhiteSpace(id) ? null : id,
        Label = string.IsNullOrWhiteSpace(label) ? null : label,
        Detail = string.IsNullOrWhiteSpace(detail) ? null : detail,
        Focus = focus
    };
}
