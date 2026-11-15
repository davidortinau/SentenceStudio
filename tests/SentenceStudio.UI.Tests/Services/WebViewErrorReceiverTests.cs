using SentenceStudio.Abstractions;
using SentenceStudio.WebUI.Services;
using Xunit;

namespace SentenceStudio.UI.Tests.Services;

/// <summary>The .NET end of webview-errors.js (#169).</summary>
public class WebViewErrorReceiverTests
{
    private sealed class RecordingSink : IWebViewErrorSink
    {
        public List<WebViewError> Reports { get; } = [];
        public void Report(WebViewError error) => Reports.Add(error);
    }

    [Fact]
    public void A_report_reaches_the_sink_with_every_field()
    {
        var sink = new RecordingSink();

        new WebViewErrorReceiver(sink).Report(new WebViewErrorPayload(
            "unhandledrejection", "rejected", "app.js", 3, 7, "at f", "/quiz"));

        var error = Assert.Single(sink.Reports);
        Assert.Equal(new WebViewError("unhandledrejection", "rejected", "app.js", 3, 7, "at f", "/quiz"), error);
    }

    [Theory]
    [InlineData("something-else")]
    [InlineData(null)]
    public void An_unknown_kind_is_recorded_as_an_error(string? kind)
    {
        var sink = new RecordingSink();

        new WebViewErrorReceiver(sink).Report(new WebViewErrorPayload(kind, "boom", null, null, null, null, null));

        Assert.Equal("error", Assert.Single(sink.Reports).Kind);
    }

    [Fact]
    public void A_missing_message_is_named_and_a_null_payload_is_ignored()
    {
        var sink = new RecordingSink();
        var receiver = new WebViewErrorReceiver(sink);

        receiver.Report(new WebViewErrorPayload("error", " ", null, null, null, null, null));
        receiver.Report(null!);

        Assert.Equal("Unknown error", Assert.Single(sink.Reports).Message);
    }
}
