using System.Text.Json.Serialization;

namespace SentenceStudio.Services.Api;

/// <summary>
/// The one-use value that authorises a protected change, as it arrives from the server.
/// </summary>
/// <remarks>
/// <para>
/// This type deliberately does not live in the shared contracts assembly. That assembly is
/// scanned, and the scan refuses any member whose name suggests a credential — a rule with no
/// exceptions and one worth keeping that way. Keeping the shape here means the value has exactly
/// one home, in the client that is about to spend it, rather than a public shape that anything
/// could deserialize into and hold.
/// </para>
/// <para>
/// It is a class rather than a record, and that is not a style choice. A positional record
/// generates a <c>ToString</c> that prints every member, so a single interpolated log line, a
/// debugger watch dumped into a bug report, or an exception message built from the object would
/// disclose the value. <see cref="ToString"/> below is overridden to say nothing.
/// </para>
/// <para>
/// The value is never stored, never rendered, never copied to the clipboard, and never placed in
/// a URL. It lives in memory for as long as the confirmation step is open and is dropped the
/// moment it is spent, expires, or the surface holding it is torn down.
/// </para>
/// </remarks>
public sealed class CoachWriteConfirmation
{
    /// <summary>The operation this authorises. Bound server-side; not interchangeable.</summary>
    public string OperationId { get; init; } = string.Empty;

    /// <summary>
    /// The one-use value, sent back as a request header and nowhere else.
    /// </summary>
    [JsonPropertyName("confirmationSecret")]
    public string Value { get; init; } = string.Empty;

    /// <summary>The summary the confirmation step shows, restated by the server.</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>The detail lines the confirmation step shows.</summary>
    public IReadOnlyList<string> Lines { get; init; } = Array.Empty<string>();

    /// <summary>When this stops being redeemable.</summary>
    public DateTime ExpiresAtUtc { get; init; }

    /// <summary>True when the value is present and its window has not closed.</summary>
    public bool IsUsableAt(DateTime utcNow) =>
        Value.Length > 0 && ExpiresAtUtc > utcNow;

    /// <summary>Says what this is and never what it holds.</summary>
    public override string ToString() => $"CoachWriteConfirmation({OperationId}, redacted)";
}
