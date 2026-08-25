using FluentAssertions;
using SentenceStudio.Api.Coach.Operations;

namespace SentenceStudio.Api.Tests.Coach.Operations;

/// <summary>
/// Proves the YouTube address check accepts only real video addresses.
/// </summary>
/// <remarks>
/// <para>
/// This check is the only thing standing between a model-supplied string and an outbound request.
/// The import tool never uses the caller's text as an address: it extracts a video id, and then
/// rebuilds a canonical address from that id. So the property under test is not "is this string
/// safe to fetch" but "does this string yield a video id at all", which is a much narrower
/// question and a much easier one to be right about.
/// </para>
/// <para>
/// The rejection cases are the interesting ones. They are the shapes an attacker would reach for:
/// a host that merely ends in the right domain, credentials in the authority, a redirect
/// parameter, a scheme that is not the web.
/// </para>
/// </remarks>
public class CoachYouTubeUrlTests
{
    private const string Id = "dQw4w9WgXcQ";

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://m.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=42s")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/v/dQw4w9WgXcQ")]
    [InlineData("  https://www.youtube.com/watch?v=dQw4w9WgXcQ  ")]
    public void A_real_video_address_yields_its_id(string url)
    {
        CoachYouTubeUrl.TryGetVideoId(url, out var videoId).Should().BeTrue();
        videoId.Should().Be(Id);
    }

    [Theory]
    // Not the web.
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>")]
    // Plain HTTP: an address that can be watched in transit is not one to fetch on request.
    [InlineData("http://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    // Hosts that only look right.
    [InlineData("https://youtube.com.evil.test/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://notyoutube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://evil.test/youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com.evil.test/watch?v=dQw4w9WgXcQ")]
    // Credentials in the authority: the part before @ is not the host.
    [InlineData("https://www.youtube.com@evil.test/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://user:pass@www.youtube.com/watch?v=dQw4w9WgXcQ")]
    // Internal addresses.
    [InlineData("https://127.0.0.1/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://localhost/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[::1]/watch?v=dQw4w9WgXcQ")]
    // A non-default port is a different service.
    [InlineData("https://www.youtube.com:8080/watch?v=dQw4w9WgXcQ")]
    // Right host, wrong shape.
    [InlineData("https://www.youtube.com/")]
    [InlineData("https://www.youtube.com/results?search_query=test")]
    [InlineData("https://www.youtube.com/redirect?q=https://evil.test")]
    [InlineData("https://www.youtube.com/watch?v=")]
    [InlineData("https://www.youtube.com/watch?list=PLxyz")]
    [InlineData("https://www.youtube.com/@somechannel")]
    // Ids of the wrong length or alphabet.
    [InlineData("https://www.youtube.com/watch?v=short")]
    [InlineData("https://www.youtube.com/watch?v=waaaaaaaaytoolong")]
    [InlineData("https://www.youtube.com/watch?v=bad!chars#")]
    [InlineData("https://www.youtube.com/watch?v=../../etc/pw")]
    // Nothing at all.
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("not a url")]
    public void Anything_else_is_refused(string? url)
    {
        CoachYouTubeUrl.TryGetVideoId(url, out var videoId).Should().BeFalse();
        videoId.Should().BeNull();
    }

    [Fact]
    public void An_absurdly_long_address_is_refused_without_parsing()
    {
        var url = "https://www.youtube.com/watch?v=" + Id + "&pad=" + new string('a', 10_000);

        CoachYouTubeUrl.TryGetVideoId(url, out _).Should().BeFalse();
    }

    /// <summary>
    /// The canonical address is rebuilt from the id, so nothing the caller wrote survives.
    /// </summary>
    /// <remarks>
    /// This is the property that makes the rest of the import safe. Even a caller's address that
    /// passes every check above is discarded: the fetch uses this string.
    /// </remarks>
    [Fact]
    public void The_canonical_address_is_rebuilt_from_the_id_alone()
    {
        CoachYouTubeUrl.TryGetVideoId(
            "https://m.youtube.com/watch?v=dQw4w9WgXcQ&t=99s&feature=share", out var videoId)
            .Should().BeTrue();

        CoachYouTubeUrl.CanonicalUrl(videoId!)
            .Should().Be("https://www.youtube.com/watch?v=dQw4w9WgXcQ");
    }

    [Theory]
    [InlineData("_-aaaaaaaaa")]
    [InlineData("00000000000")]
    [InlineData("aAzZ09_-xyz")]
    public void The_id_alphabet_is_the_documented_one(string id)
    {
        CoachYouTubeUrl.TryGetVideoId($"https://youtu.be/{id}", out var parsed).Should().BeTrue();
        parsed.Should().Be(id);
    }
}
