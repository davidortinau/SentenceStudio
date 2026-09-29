using System.Globalization;
using System.Reflection;
using System.Resources;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Abstractions;
using SentenceStudio.Native;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Services;

namespace SentenceStudio.Native.Tests;

public sealed class NativeQuizModalityTests
{
    private static readonly VocabularyWord Word = new()
    {
        Id = "word-1",
        TargetLanguageTerm = "bonjour",
        NativeLanguageTerm = "hello",
        Language = "French",
        MnemonicImageUri = "opaque-image-1"
    };

    public static IEnumerable<object[]> ApprovedMatrix()
    {
        for (var mask = 0; mask < 8; mask++)
            yield return [($"Q{mask + 1:D2}"), false,
                (mask & 1) != 0, (mask & 2) != 0, (mask & 4) != 0, false];
        for (var mask = 0; mask < 4; mask++)
            yield return [($"Q{mask + 9:D2}"), true,
                (mask & 1) != 0, (mask & 2) != 0, false, false];
        for (var mask = 0; mask < 4; mask++)
            yield return [($"Q{mask + 13:D2}"), true,
                (mask & 1) != 0, (mask & 2) != 0, true, false];
        for (var mask = 0; mask < 4; mask++)
            yield return [($"Q{mask + 17:D2}"), true,
                (mask & 1) != 0, (mask & 2) != 0, true, true];
    }

    [Theory]
    [MemberData(nameof(ApprovedMatrix))]
    public void Q01ToQ20_VisibleOrHeardPromptAndTargetResponseRetainL2(
        string caseId, bool nativePrompt, bool text, bool audio, bool photo,
        bool showTextWithPhoto)
    {
        var preferences = NewPreferences();
        preferences.DisplayDirection = nativePrompt ? "NativeToTarget" : "TargetToNative";
        preferences.UseTextPrompt = text;
        preferences.UseAudioPrompt = audio;
        preferences.UsePhotoPrompt = photo;

        var cue = NativeQuizModality.Render(
            Word, nativePrompt, preferences, showTextWithPhoto,
            audioAvailable: true, audioHeard: audio,
            photoLoaded: true, photoVisible: photo,
            targetResponseVisible: true,
            nativeLanguage: "English", targetLanguage: "French");
        Assert.True(cue.TargetLanguageEngaged, caseId);
        Assert.Equal(!nativePrompt, cue.IsTargetLanguagePrompt);
        if (!nativePrompt && photo)
        {
            Assert.True(cue.ShowWrittenPrompt);
            Assert.Equal(Word.TargetLanguageTerm, cue.PromptText);
        }
        if (nativePrompt && photo && !showTextWithPhoto)
            Assert.Null(cue.PromptText);
        if (nativePrompt)
            Assert.NotEqual(Word.TargetLanguageTerm, cue.PromptText);
        if (!nativePrompt)
            Assert.NotEqual(Word.NativeLanguageTerm, cue.PromptText);
        Assert.Equal(NativeText.QuizPhoto, cue.PhotoAccessibilityKey);

        var sentence = NativeQuizModality.RenderSentence(Word, "French");
        Assert.Equal(Word.TargetLanguageTerm, sentence.TargetLanguageTerm);
        Assert.Equal(Word.NativeLanguageTerm, sentence.NativeLanguageGloss);
        Assert.Equal("VocabQuiz_WriteOneSentence", sentence.InstructionKey);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedTurns_EvaluateTheirActualDirectionAndNeverHideTargetPhotoWord(bool nativePrompt)
    {
        var preferences = NewPreferences();
        preferences.DisplayDirection = "Mixed";
        preferences.UseTextPrompt = false;
        preferences.UseAudioPrompt = false;
        preferences.UsePhotoPrompt = true;
        var cue = NativeQuizModality.Render(
            Word, nativePrompt, preferences, showTextWithPhoto: false,
            audioAvailable: false, audioHeard: false,
            photoLoaded: true, photoVisible: true,
            targetResponseVisible: true, "English", "French");
        Assert.True(cue.TargetLanguageEngaged);
        Assert.Equal(nativePrompt ? null : Word.TargetLanguageTerm, cue.PromptText);
    }

    [Fact]
    public void FirstRunPreferences_AreL2PromptTextAndAudioWithPhotoOff()
    {
        var preferences = NewPreferences();
        Assert.Equal("TargetToNative", preferences.DisplayDirection);
        Assert.True(preferences.UseTextPrompt);
        Assert.True(preferences.UseAudioPrompt);
        Assert.False(preferences.UsePhotoPrompt);
        Assert.Equal(2000, preferences.AutoAdvanceDuration);

        var defaultTurn = NativeQuizModality.Render(
            Word, promptUsesNativeLanguage: false, preferences,
            showTextWithPhoto: false, audioAvailable: true,
            audioHeard: false, photoLoaded: true, photoVisible: false,
            targetResponseVisible: false, "English", "French");
        Assert.True(defaultTurn.TargetLanguageEngaged);
        Assert.True(defaultTurn.ShowWrittenPrompt);
        Assert.False(defaultTurn.ShowPhoto);
        Assert.Equal("bonjour", defaultTurn.PromptText);
    }

    [Fact]
    public void HiddenOrBrokenMedia_NeverEnablesNativeLanguageOnlyPhotoMatching()
    {
        var preferences = NewPreferences();
        preferences.UseTextPrompt = false;
        preferences.UseAudioPrompt = false;
        preferences.UsePhotoPrompt = true;
        var targetPhoto = NativeQuizModality.Render(
            Word, false, preferences, false, false, false,
            true, true, false, "English", "French");
        Assert.Equal("bonjour", targetPhoto.PromptText);
        Assert.True(targetPhoto.TargetLanguageEngaged);

        var nativePhoto = NativeQuizModality.Render(
            Word, true, preferences, false, false, false,
            true, true, true, "English", "French");
        Assert.Null(nativePhoto.PromptText);
        Assert.True(nativePhoto.TargetLanguageEngaged);
        Assert.False(NativeQuizModality.Render(
            Word, true, preferences, false, false, false,
            true, true, false, "English", "French").TargetLanguageEngaged);

        foreach (var nativePrompt in new[] { false, true })
        {
            var hidden = NativeQuizModality.Render(
                Word, nativePrompt, preferences, false,
                false, false, true, false, true, "English", "French");
            Assert.False(hidden.TargetLanguageEngaged);
            var brokenPhoto = NativeQuizModality.Render(
                Word, nativePrompt, preferences, false,
                false, false, false, false, true, "English", "French");
            Assert.True(brokenPhoto.ShowWrittenPrompt);
            Assert.True(brokenPhoto.TargetLanguageEngaged);
        }
    }

    [Fact]
    public void AudioOnlyRequiresConfirmedPlayback_AndBlindLetterChoicesAreBlocked()
    {
        var preferences = NewPreferences();
        preferences.UseTextPrompt = false;
        preferences.UseAudioPrompt = true;
        preferences.UsePhotoPrompt = false;

        var unheardTarget = NativeQuizModality.Render(
            Word, false, preferences, false,
            true, false, false, false, false, "English", "French");
        Assert.False(unheardTarget.TargetLanguageEngaged);
        var heardTarget = NativeQuizModality.Render(
            Word, false, preferences, false,
            true, true, false, false, false, "English", "French");
        Assert.True(heardTarget.TargetLanguageEngaged);
        Assert.Equal("bonjour", heardTarget.PromptAudioText);

        var nativeBlindLetters = NativeQuizModality.Render(
            Word, true, preferences, false,
            true, true, false, false, false, "English", "French");
        Assert.False(nativeBlindLetters.TargetLanguageEngaged);
        var nativeWithL2Choices = NativeQuizModality.Render(
            Word, true, preferences, false,
            true, true, false, false, true, "English", "French");
        Assert.True(nativeWithL2Choices.TargetLanguageEngaged);
        Assert.Equal("hello", nativeWithL2Choices.PromptAudioText);

        var offline = NativeQuizModality.Render(
            Word, false, preferences, false,
            false, false, false, false, false, "English", "French");
        Assert.True(offline.ShowWrittenPrompt);
        Assert.True(offline.TargetLanguageEngaged);
    }

    [Fact]
    public void EveryNativeTextKeyHasExistingEnglishAndKoreanResource()
    {
        var manager = new ResourceManager(
            "SentenceStudio.Resources.Strings.AppResources",
            typeof(VocabQuizPhotoTextPolicy).Assembly);
        var keys = typeof(NativeText).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();
        Assert.NotEmpty(keys);
        foreach (var key in keys)
        {
            Assert.False(string.IsNullOrWhiteSpace(
                manager.GetString(key, CultureInfo.GetCultureInfo("en"))), key);
            Assert.False(string.IsNullOrWhiteSpace(
                manager.GetString(key, CultureInfo.GetCultureInfo("ko"))), key);
        }
        var identifiers = typeof(NativeAccess)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToList();
        Assert.Equal(identifiers.Count,
            identifiers.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ApprovedNineteenDelays_RoundTripAcrossSharedPreferenceInstances()
    {
        var storage = new NativeSliceFixture.ScratchPreferences();
        var preferences = NewPreferences(storage);
        Assert.Equal(2000, preferences.AutoAdvanceDuration);
        Assert.Equal(19, NativeQuizTiming.ApprovedDurationsMs.Count);
        Assert.Equal(
            Enumerable.Range(0, 19).Select(step => 1000 + step * 500).ToArray(),
            NativeQuizTiming.ApprovedDurationsMs.ToArray());
        foreach (var milliseconds in NativeQuizTiming.ApprovedDurationsMs)
        {
            Assert.Equal(NativeQuizTimingUpdate.Applied,
                NativeQuizTiming.TrySet(preferences, milliseconds));
            Assert.Equal(milliseconds, NewPreferences(storage).AutoAdvanceDuration);
        }

        preferences.ResetToDefaults();
        Assert.Equal(2000, NewPreferences(storage).AutoAdvanceDuration);
    }

    [Theory]
    [InlineData(int.MinValue)]
    [InlineData(999)]
    [InlineData(1001)]
    [InlineData(5050)]
    [InlineData(10001)]
    [InlineData(int.MaxValue)]
    public void InvalidNativeTiming_DoesNotReplacePersistedDuration(int milliseconds)
    {
        var storage = new NativeSliceFixture.ScratchPreferences();
        var preferences = NewPreferences(storage);
        Assert.Equal(NativeQuizTimingUpdate.Applied,
            NativeQuizTiming.TrySet(preferences, 7500));

        Assert.Equal(NativeQuizTimingUpdate.Invalid,
            NativeQuizTiming.TrySet(preferences, milliseconds));
        Assert.Equal(7500, NewPreferences(storage).AutoAdvanceDuration);
    }

    [Fact]
    public void SharedStorageRejectingApprovedDuration_IsNotReportedAsApplied()
    {
        var preferences = NewPreferences(new ReadOnlyPreferences());

        Assert.Equal(NativeQuizTimingUpdate.RequiresSharedPreferenceFix,
            NativeQuizTiming.TrySet(preferences, 5500));
        Assert.Equal(2000, preferences.AutoAdvanceDuration);
    }

    [Theory]
    [InlineData(int.MinValue, 1000)]
    [InlineData(999, 1000)]
    [InlineData(1001, 1000)]
    [InlineData(1250, 1500)]
    [InlineData(5050, 5000)]
    [InlineData(5501, 5500)]
    [InlineData(9750, 10000)]
    [InlineData(10001, 10000)]
    [InlineData(int.MaxValue, 10000)]
    public void InvalidSharedDurations_AreNormalizedBeforePersistence(
        int milliseconds, int expected)
    {
        var storage = new NativeSliceFixture.ScratchPreferences();
        var preferences = NewPreferences(storage);
        preferences.AutoAdvanceDuration = milliseconds;

        Assert.Equal(expected, preferences.AutoAdvanceDuration);
        Assert.Equal(expected, NewPreferences(storage).AutoAdvanceDuration);
    }

    private static VocabularyQuizPreferences NewPreferences(
        IPreferencesService? storage = null)
    {
        storage ??= new NativeSliceFixture.ScratchPreferences();
        return new VocabularyQuizPreferences(
            NullLogger<VocabularyQuizPreferences>.Instance,
            new SpeechVoicePreferences(
                NullLogger<SpeechVoicePreferences>.Instance, storage),
            storage);
    }

    private sealed class ReadOnlyPreferences : IPreferencesService
    {
        public T Get<T>(string key, T defaultValue) => defaultValue;
        public void Set<T>(string key, T value) { }
        public void Remove(string key) { }
        public void Clear() { }
    }
}
