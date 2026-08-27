using System.Text.Json;
using FluentAssertions;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Operations.Handlers;
using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Tests.Coach.Operations;

/// <summary>
/// Proves the import handler records arguments its own execution can still read.
/// </summary>
/// <remarks>
/// <para>
/// A proposal stores canonical arguments, and execution — which happens minutes later, on a
/// different request, after the learner confirms — is handed those stored arguments back and
/// re-validates them with the same code preparation used. That makes "what preparation writes"
/// and "what execution accepts" the same contract, and nothing was checking it.
/// </para>
/// <para>
/// Preparation used to store the bare eleven-character video id in the address slot. The id is not
/// an absolute URL, so <see cref="CoachYouTubeUrl.TryGetVideoId"/> refused it and every confirmed
/// import failed with "That is not a YouTube video address" — the feature could be proposed and
/// confirmed but could never complete. The existing Postgres suite only ever proposed, so the
/// round trip was never exercised; browser E2E on 2026-08-19 hit it on the first real confirm.
/// </para>
/// <para>
/// These tests call preparation only. It performs no network I/O by design, which is what lets the
/// round-trip property be proved without an outbound request.
/// </para>
/// </remarks>
public class CoachYouTubeImportArgumentRoundTripTests
{
    private const string Owner = "owner-1";
    private const string Id = "dQw4w9WgXcQ";

    /// <summary>
    /// Preparation touches none of the handler's collaborators, so none are needed to prove what
    /// it records. Passing them would only obscure that.
    /// </summary>
    private static CoachYouTubeImportHandler Handler() => new(null!, null!, null!);

    private static async Task<CoachWritePreview> PrepareAsync(string url, string? captions = null)
    {
        var json = JsonSerializer.Serialize(new CoachYouTubeImportArgs(url, captions));
        return await Handler().PrepareAsync(Owner, json, CancellationToken.None);
    }

    private static CoachYouTubeImportArgs Recorded(CoachWritePreview preview) =>
        JsonSerializer.Deserialize<CoachYouTubeImportArgs>(preview.CanonicalArgumentsJson)!;

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ&t=42s")]
    public async Task The_arguments_a_proposal_records_are_still_a_valid_import_address(string url)
    {
        var preview = await PrepareAsync(url);

        var recorded = Recorded(preview);

        CoachYouTubeUrl.TryGetVideoId(recorded.VideoUrl, out var videoId).Should().BeTrue(
            "execution re-validates the stored arguments, so anything preparation writes must "
            + "survive the same check");
        videoId.Should().Be(Id);
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ")]
    public async Task Every_accepted_shape_records_the_same_canonical_address(string url)
    {
        var preview = await PrepareAsync(url);

        Recorded(preview).VideoUrl.Should().Be($"https://www.youtube.com/watch?v={Id}");
    }

    [Fact]
    public async Task The_recorded_address_is_the_one_the_learner_was_shown()
    {
        var preview = await PrepareAsync($"https://youtu.be/{Id}?si=tracking-token");

        var shown = preview.Lines.Single(l => l.StartsWith("Video: ", StringComparison.Ordinal));

        shown.Should().Be($"Video: {Recorded(preview).VideoUrl}",
            "the confirmation digest binds the stored arguments, so the learner must be looking "
            + "at the address that will actually be fetched");
    }

    [Fact]
    public async Task Nothing_of_the_callers_original_string_survives_into_what_is_stored()
    {
        var preview = await PrepareAsync(
            $"https://m.youtube.com/watch?v={Id}&utm_source=somewhere&redirect=https://elsewhere.example");

        var recorded = Recorded(preview).VideoUrl;

        recorded.Should().Be($"https://www.youtube.com/watch?v={Id}");
        recorded.Should().NotContain("utm_source").And.NotContain("elsewhere.example");
    }

    [Fact]
    public async Task The_caption_language_survives_the_round_trip()
    {
        var preview = await PrepareAsync($"https://www.youtube.com/watch?v={Id}", "ko");

        Recorded(preview).CaptionLanguage.Should().Be("ko");
        preview.Lines.Should().Contain("Captions: ko");
    }

    [Theory]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com.attacker.example/watch?v=dQw4w9WgXcQ")]
    [InlineData("http://youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://user@youtube.com/watch?v=dQw4w9WgXcQ")]
    public async Task An_address_that_is_not_a_youtube_video_never_reaches_a_preview(string url)
    {
        var act = async () => await PrepareAsync(url);

        await act.Should().ThrowAsync<CoachToolException>();
    }
}
