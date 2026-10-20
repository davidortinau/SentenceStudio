using System.Collections.Immutable;
using System.Net.Http;
using System.Text.Json;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Services;

namespace SentenceStudio.Native;

/// <summary>Source quiz preferences; the existing host preference keys remain authoritative.</summary>
public sealed class NativeQuizSettings(
    DeviceUserScopeProvider scope,
    IPreferencesService preferences,
    VocabularyQuizPreferences quiz,
    SpeechVoicePreferences voices,
    INativeQuizSpeechSource? speech = null)
{
    public static ImmutableArray<string> Directions { get; } =
        ["TargetToNative", "NativeToTarget", "Mixed"];
    public static ImmutableArray<string> Languages { get; } =
        ["Korean", "English", "French", "German", "Spanish"];

    private bool HasOwner(
        string owner, DeviceUserScopeProvider.ScopeStamp? expectedStamp = null) =>
        !string.IsNullOrWhiteSpace(owner)
        && (!expectedStamp.HasValue
            || expectedStamp.Value.PublishedOwner == owner
                && scope.IsCurrent(expectedStamp.Value))
        && scope.TryGetUserProfileId(out var active) && active == owner
        && preferences.Get(UserProfileRepository.ActiveProfileIdKey, string.Empty) == owner;

    public NativeQuizSettingsState? Read(string owner, string language = "Korean")
    {
        if (!HasOwner(owner) || !Languages.Contains(language))
            return null;
        return new NativeQuizSettingsState(
            quiz.DisplayDirection,
            quiz.AutoPlayVocabAudio,
            quiz.ShowMnemonicImage,
            quiz.UseTextPrompt,
            quiz.UseAudioPrompt,
            quiz.UsePhotoPrompt,
            quiz.AutoAdvanceDuration,
            language,
            voices.GetVoiceForLanguage(language));
    }

    public async Task<NativeQuizSettingsState?> GetAsync(
        string owner, string language = "Korean",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = Read(owner, language);
        if (state is null)
            return null;
        if (speech is null)
            return state with { ErrorKey = NativeText.ConnectionError };
        try
        {
            var options = await speech.GetVoicesAsync(owner, language, cancellationToken);
            if (!HasOwner(owner))
                return null;
            return state with
            {
                VoiceOptions = options
                    .Where(option => !string.IsNullOrWhiteSpace(option.Id))
                    .Select(option => new NativeQuizVoiceOption(option.Id, option.Name))
                    .ToImmutableArray()
            };
        }
        catch (HttpRequestException) when (HasOwner(owner))
        {
            return state with { ErrorKey = NativeText.ConnectionError };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested
            && HasOwner(owner))
        {
            return state with { ErrorKey = NativeText.ConnectionError };
        }
        catch (JsonException) when (HasOwner(owner))
        {
            return state with { ErrorKey = NativeText.ConnectionError };
        }
    }

    public NativeQuizSettingsState? Save(
        string owner, NativeQuizSettingsState requested,
        DeviceUserScopeProvider.ScopeStamp? expectedStamp = null)
    {
        ArgumentNullException.ThrowIfNull(requested);
        if (!HasOwner(owner, expectedStamp))
            return null;
        if (!Directions.Contains(requested.Direction)
            || !Languages.Contains(requested.VoiceLanguage)
            || string.IsNullOrWhiteSpace(requested.VoiceId)
            || requested.VoiceId.Length > 128
            || !NativeQuizTiming.ApprovedDurationsMs.Contains(
                requested.AutoAdvanceDurationMs))
            return HasOwner(owner, expectedStamp) && Read(owner) is { } invalid
                ? invalid with { ErrorKey = NativeText.QuizUnavailable } : null;

        var timing = NativeQuizTiming.TrySet(quiz, requested.AutoAdvanceDurationMs);
        if (!HasOwner(owner, expectedStamp))
            return null;
        if (timing != NativeQuizTimingUpdate.Applied)
            return Read(owner) is { } failed
                ? failed with { ErrorKey = NativeText.SaveFailed } : null;
        quiz.DisplayDirection = requested.Direction;
        if (!HasOwner(owner, expectedStamp))
            return null;
        quiz.AutoPlayVocabAudio = requested.AutoPlayAudio;
        if (!HasOwner(owner, expectedStamp))
            return null;
        quiz.ShowMnemonicImage = requested.ShowMnemonics;
        if (!HasOwner(owner, expectedStamp))
            return null;
        // Source Settings always restores a readable prompt when all three modalities are off.
        var textPrompt = requested.UseTextPrompt
            || !requested.UseAudioPrompt && !requested.UsePhotoPrompt;
        quiz.UseTextPrompt = textPrompt;
        if (!HasOwner(owner, expectedStamp))
            return null;
        quiz.UseAudioPrompt = requested.UseAudioPrompt;
        if (!HasOwner(owner, expectedStamp))
            return null;
        quiz.UsePhotoPrompt = requested.UsePhotoPrompt;
        if (!HasOwner(owner, expectedStamp))
            return null;
        voices.SetVoiceForLanguage(requested.VoiceLanguage, requested.VoiceId);
        if (!HasOwner(owner, expectedStamp))
            return null;
        var stored = Read(owner, requested.VoiceLanguage);
        if (stored is null || !HasOwner(owner, expectedStamp))
            return null;
        return stored.Direction == requested.Direction
            && stored.AutoPlayAudio == requested.AutoPlayAudio
            && stored.ShowMnemonics == requested.ShowMnemonics
            && stored.UseTextPrompt == textPrompt
            && stored.UseAudioPrompt == requested.UseAudioPrompt
            && stored.UsePhotoPrompt == requested.UsePhotoPrompt
            && stored.AutoAdvanceDurationMs == requested.AutoAdvanceDurationMs
            && stored.VoiceId == requested.VoiceId
            ? stored : stored with { ErrorKey = NativeText.SaveFailed };
    }
}
