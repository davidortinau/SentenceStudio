using SentenceStudio.Contracts.Speech;
using SentenceStudio.Services.Speech;

namespace SentenceStudio.Api.Tests.Speech;

/// <summary>The one shared reading of the timestamped wire format (API, web and MAUI, native).</summary>
public sealed class TimestampedSpeechTests
{
    [Fact]
    public void The_transcript_fingerprint_is_lowercase_hex_sha256_of_the_utf8_text()
    {
        // SHA-256 of the UTF-8 bytes of "abc", the FIPS 180-2 test vector.
        TimestampedSpeech.TranscriptSha256("abc")
            .Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
        TimestampedSpeech.TranscriptSha256("가나다").Should().NotBe(TimestampedSpeech.TranscriptSha256("가나다 "));
    }

    [Fact]
    public void Wire_milliseconds_and_second_tuples_build_the_same_characters()
    {
        var fromWire = TimestampedSpeech.ToCharacters(new[]
        {
            new CharacterTimestamp { Char = "가", StartMs = 0, EndMs = 250 },
            new CharacterTimestamp { Char = " ", StartMs = 250, EndMs = 600 }
        });
        var fromSeconds = TimestampedSpeech.ToCharacters(new[] { ("가", 0.0, 0.25), (" ", 0.25, 0.6) });

        fromWire.Select(c => (c.Character.ToString(), c.StartTime, c.EndTime)).Should().Equal(
            ("가", 0.0, 0.25), (" ", 0.25, 0.6));
        fromSeconds.Select(c => (c.Character.ToString(), c.StartTime, c.EndTime)).Should().Equal(
            fromWire.Select(c => (c.Character.ToString(), c.StartTime, c.EndTime)));
    }
}
