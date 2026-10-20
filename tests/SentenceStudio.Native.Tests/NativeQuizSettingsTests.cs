using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Tests;

public sealed class NativeQuizSettingsTests
{
    [Fact]
    public void ExistingPreferenceKeys_SaveMixedAudioPhotoAndTenSecondDurationAcrossRestart()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var settings = fixture.Provider.GetRequiredService<NativeQuizSettings>();
        var first = settings.Read(NativeSliceFixture.UserA);
        Assert.NotNull(first);
        Assert.Equal("TargetToNative", first.Direction);
        Assert.True(first.UseTextPrompt);
        Assert.False(first.UsePhotoPrompt);
        Assert.Equal(2000, first.AutoAdvanceDurationMs);

        var saved = settings.Save(NativeSliceFixture.UserA, first with
        {
            Direction = "Mixed",
            AutoPlayAudio = false,
            ShowMnemonics = false,
            UseTextPrompt = false,
            UseAudioPrompt = true,
            UsePhotoPrompt = true,
            AutoAdvanceDurationMs = 10000,
            VoiceLanguage = "English",
            VoiceId = "voice-for-english"
        });
        Assert.NotNull(saved);
        Assert.Equal("Mixed", saved.Direction);
        Assert.Equal(10000, saved.AutoAdvanceDurationMs);
        Assert.Equal("voice-for-english", saved.VoiceId);
        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        var restored = fixture.Provider.GetRequiredService<NativeQuizSettings>()
            .Read(NativeSliceFixture.UserA, "English");
        Assert.Equal(saved, restored);
    }

    [Fact]
    public void MissingOwnerOrInvalidValues_CannotChangeSavedSettings()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var settings = fixture.Provider.GetRequiredService<NativeQuizSettings>();
        var before = settings.Read(NativeSliceFixture.UserA)!;
        Assert.Null(settings.Save(NativeSliceFixture.UserB, before with
        {
            Direction = "Mixed"
        }));
        Assert.Null(settings.Read(NativeSliceFixture.UserB));
        Assert.Equal(NativeText.QuizUnavailable, settings.Save(
            NativeSliceFixture.UserA,
            before with { Direction = "unrecognized" })?.ErrorKey);
        Assert.Equal(NativeText.QuizUnavailable, settings.Save(
            NativeSliceFixture.UserA,
            before with { AutoAdvanceDurationMs = 10999 })?.ErrorKey);
        Assert.Equal(before, settings.Read(NativeSliceFixture.UserA));
    }

    [Fact]
    public void EveryDisabledModality_FallsBackToSourceWrittenPrompt()
    {
        using var fixture = new NativeSliceFixture();
        fixture.Activate(NativeSliceFixture.UserA);
        var settings = fixture.Provider.GetRequiredService<NativeQuizSettings>();
        var before = settings.Read(NativeSliceFixture.UserA)!;
        var saved = settings.Save(NativeSliceFixture.UserA, before with
        {
            UseTextPrompt = false, UseAudioPrompt = false, UsePhotoPrompt = false
        });
        Assert.True(saved!.UseTextPrompt);
        Assert.False(saved.UseAudioPrompt);
        Assert.False(saved.UsePhotoPrompt);
    }
}
