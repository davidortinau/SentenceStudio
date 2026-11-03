using System.Runtime.CompilerServices;
using System.Text.Json;
using SentenceStudio.Shared.Models;
using Xunit;

namespace SentenceStudio.UnitTests.Models;

/// <summary>
/// The saved Vocabulary Quiz format every client shares (docs/activity-session-format.md).
/// The sample files in tests/fixtures/activity-session are the contract: every client's tests
/// read them (the Flutter port reads the same files when it joins).
/// </summary>
public sealed class VocabQuizSavedStateContractTests
{
    [Fact]
    public void WebSample_ReadsWithoutTheFieldsOnlyNativeAppsRecord()
    {
        var state = Read("web-between-questions.json");

        Assert.Equal("plan=9d3fd8e6c3ae5ee8|focus=|res=|due=false|skill=", state.LaunchContextKey);
        Assert.Equal(new DateOnly(2026, 11, 3), state.PlanDate);
        Assert.Equal(("9d3fd8e6c3ae5ee8", 3, 1, 1), (state.Snapshot.PlanItemId,
            state.Snapshot.BatchPool.Count, state.Snapshot.CurrentTurnInRound, state.Snapshot.TotalTurns));
        Assert.Null(state.PlanItemRowId);
        Assert.Null(state.IsRepractice);
        Assert.Null(state.SavedBy);
        Assert.False(state.HasUnfinishedTurn);
    }

    [Fact]
    public void GradedAnswerSample_BelongsOnlyToTheInstallThatSavedIt()
    {
        var state = Read("native-graded-answer.json");

        Assert.True(state.HasUnfinishedTurn);
        Assert.Equal(("word-1", true), (state.PendingAttempt!.VocabularyWordId, state.PendingAttempt.WasCorrect));
        Assert.True(state.IsSavedBy("native:5e0c2a7b9d1f4e3a8c6b4d2f0e1a3c5b"));
        Assert.False(state.IsSavedBy("native:another-install"));
        Assert.False(state.IsSavedBy(null));
    }

    [Fact]
    public void SentenceTurnSample_KeepsItsGradedSentences()
    {
        var state = Read("native-sentence-turn.json");

        Assert.True(state.HasUnfinishedTurn);
        Assert.True(state.SentenceCreditApplied);
        var grade = Assert.Single(state.SentenceResults);
        Assert.Equal(("사과", true), (grade.TargetUsedForm, grade.HasVerifiedTargetUse));
        Assert.Equal("adhoc-0c1d2e3f", state.AdHocPlanItemId);
    }

    [Fact]
    public void AFieldANewerClientAdded_IsIgnored()
    {
        var state = Read("newer-client-extra-field.json");
        Assert.True(state.Snapshot.DueOnly);
        Assert.Equal("", state.DisplayDirection);
    }

    [Theory]
    [InlineData("invalid-missing-turn.json")]
    [InlineData("invalid-unknown-version.json")]
    [InlineData("invalid-bare-snapshot.json")]
    public void InvalidSamples_AreRefused(string file)
    {
        Assert.Throws<JsonException>(() => VocabQuizSavedState.Deserialize(Sample(file)));
        Assert.False(VocabQuizSavedState.TryDeserialize(Sample(file), out var state));
        Assert.Null(state);
    }

    [Theory]
    [InlineData("web-between-questions.json")]
    [InlineData("native-graded-answer.json")]
    [InlineData("native-sentence-turn.json")]
    public void EverySample_SurvivesAWriteAndReadBack(string file)
    {
        var state = Read(file);
        Assert.Equal(state.Serialize(), VocabQuizSavedState.Deserialize(state.Serialize()).Serialize());
        // Field names are the contract's camelCase names.
        using var written = JsonDocument.Parse(state.Serialize());
        Assert.True(written.RootElement.TryGetProperty("launchContextKey", out _));
        Assert.True(written.RootElement.GetProperty("snapshot").TryGetProperty("batchPool", out _));
    }

    private static VocabQuizSavedState Read(string file) => VocabQuizSavedState.Deserialize(Sample(file));

    private static string Sample(string file) => File.ReadAllText(Path.Combine(
        Path.GetDirectoryName(ThisFile())!, "../../fixtures/activity-session", file));

    private static string ThisFile([CallerFilePath] string path = "") => path;
}
