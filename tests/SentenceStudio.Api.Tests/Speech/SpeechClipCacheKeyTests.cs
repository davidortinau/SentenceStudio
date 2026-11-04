using ElevenLabs;
using SentenceStudio.Api.Speech;

namespace SentenceStudio.Api.Tests.Speech;

/// <summary>The cache key: what changes it, what it must never contain, and that it stays stable.</summary>
public sealed class SpeechClipCacheKeyTests
{
    private static readonly SpeechClipSpec Baseline =
        SpeechClipSpec.ForSynthesize("xi3rF0t7dg7uN2M0WUhr", "eleven_v3", "안녕하세요");

    [Fact]
    public void Same_spec_always_names_the_same_blob()
    {
        var again = SpeechClipSpec.ForSynthesize("xi3rF0t7dg7uN2M0WUhr", "eleven_v3", "안녕하세요");

        SpeechClipCacheKey.BlobName(again).Should().Be(SpeechClipCacheKey.BlobName(Baseline));
    }

    [Fact]
    public void Key_is_pinned_so_a_refactor_cannot_silently_orphan_every_stored_clip()
    {
        // If this fails, the key material changed. Stored clips under v1 would no longer be found
        // and every learner would pay again. Bump SpeechClipCacheKey.Version on purpose instead.
        SpeechClipCacheKey.BlobName(Baseline).Should().Be(
            "speech-cache/v1/synthesize/" + PinnedBaselineHash + ".mp3");
    }

    // SHA-256 of the UTF-8 JSON
    // {"Provider":"elevenlabs","Variant":"Synthesize","VoiceId":"xi3rF0t7dg7uN2M0WUhr",
    //  "ModelId":"eleven_v3","Stability":0.75,"SimilarityBoost":0.75,"Style":0,"SpeakerBoost":true,
    //  "Speed":1,"OutputFormat":"mp3_44100_128","Text":"안녕하세요"}
    // reproduced independently outside .NET when it was pinned.
    private const string PinnedBaselineHash = "991278328e4fa2ee4ac4969432df8ef7c4ee113245d6f632805b27cc269282c7";

    [Fact]
    public void Blob_name_carries_only_a_hash_never_the_text_or_voice()
    {
        var name = SpeechClipCacheKey.BlobName(Baseline);

        name.Should().MatchRegex("^speech-cache/v1/synthesize/[0-9a-f]{64}\\.mp3$");
        name.Should().NotContain("안녕").And.NotContain(Baseline.VoiceId);

        var timed = SpeechClipSpec.ForTimestamped("xi3rF0t7dg7uN2M0WUhr", 0.5f, 0.75f, 1.0f, "안녕하세요");
        SpeechClipCacheKey.BlobName(timed).Should().MatchRegex(
            "^speech-cache/v1/synthesize-timestamped/[0-9a-f]{64}\\.json$");
    }

    public static TheoryData<string, SpeechClipSpec> OneFieldChanged() => new()
    {
        { "voice", Baseline with { VoiceId = "AW5wrnG1jVizOYY7R1Oo" } },
        { "model", Baseline with { ModelId = "eleven_flash_v2_5" } },
        { "stability", Baseline with { Stability = 0.5f } },
        { "similarity boost", Baseline with { SimilarityBoost = 0.7f } },
        { "style", Baseline with { Style = 0.45f } },
        { "speaker boost", Baseline with { SpeakerBoost = false } },
        { "speed", Baseline with { Speed = 1.1f } },
        { "output format", Baseline with { OutputFormat = OutputFormat.MP3_44100_192 } },
        { "text", Baseline with { Text = "안녕하세요!" } },
        { "trailing space in text", Baseline with { Text = "안녕하세요 " } },
        { "variant", Baseline with { Variant = SpeechClipVariant.Timestamped } }
    };

    [Theory]
    [MemberData(nameof(OneFieldChanged))]
    public void Any_field_that_shapes_the_audio_changes_the_key(string field, SpeechClipSpec changed)
    {
        SpeechClipCacheKey.Hash(changed).Should().NotBe(SpeechClipCacheKey.Hash(Baseline),
            $"a different {field} produces different audio, so it must not share a stored clip");
    }

    [Fact]
    public void Timestamped_settings_come_from_the_request_and_change_the_key()
    {
        var a = SpeechClipSpec.ForTimestamped("v", 0.5f, 0.75f, 1.0f, "transcript");
        var b = SpeechClipSpec.ForTimestamped("v", 0.5f, 0.75f, 0.9f, "transcript");

        SpeechClipCacheKey.Hash(a).Should().NotBe(SpeechClipCacheKey.Hash(b));
        a.ModelId.Should().Be("eleven_multilingual_v2", "the timestamped endpoint has always used Multilingual v2");
    }
}
