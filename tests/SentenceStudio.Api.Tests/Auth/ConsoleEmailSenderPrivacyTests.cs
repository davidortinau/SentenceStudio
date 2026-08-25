using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Diagnostics;
using SentenceStudio.Shared.Models;
using SentenceStudio.Services;

namespace SentenceStudio.Api.Tests.Auth;

/// <summary>
/// <see cref="ConsoleEmailSender"/> is registered unconditionally in both hosts, so it runs in the
/// deployed environment as well as locally. Before this change it wrote the recipient address, the
/// display name, the rendered body and the live confirmation/reset link to the log at Information
/// level — and a reset link is a bearer credential, so log access alone was enough to take over an
/// account. These tests pin the environment gate that now stands between those two behaviours.
/// </summary>
public sealed class ConsoleEmailSenderPrivacyTests
{
    private const string Email = "squad-jayne@sentencestudio.test";
    private const string LocalPart = "squad-jayne";
    private const string DisplayName = "Jayne Cobb";
    private const string ResetLink = "https://example.test/Account/ResetPassword?token=SECRET-TOKEN-VALUE";

    private static ApplicationUser User => new() { Email = Email, DisplayName = DisplayName };

    [Fact]
    public async Task Outside_development_it_logs_no_address_no_name_and_no_link()
    {
        var recorder = new AuthLogRecorder();
        var sender = new ConsoleEmailSender(
            Logger(recorder), new StubEnvironment("Production"));

        await sender.SendPasswordResetLinkAsync(User, Email, ResetLink);
        await sender.SendConfirmationLinkAsync(User, Email, ResetLink);
        await sender.SendEmailAsync(Email, "Subject line", "<p>body</p>");

        var entries = recorder.Entries;
        entries.Should().NotBeEmpty("the sender must still record that it was invoked");

        foreach (var entry in entries)
        {
            foreach (var (name, text) in entry.Surfaces())
            {
                text.Should().NotContainEquivalentOf(Email, $"{name} must not carry the address");
                text.Should().NotContainEquivalentOf(LocalPart, $"{name} must not carry the local part");
                text.Should().NotContainEquivalentOf(DisplayName, $"{name} must not carry the display name");
                text.Should().NotContainEquivalentOf("SECRET-TOKEN-VALUE",
                    $"{name} must not carry the link, which is a bearer credential");
            }
        }

        entries.Should().Contain(e => e.Message.Contains(AuthLogRedaction.MaskEmail(Email)),
            "the masked recipient should survive so the log is still useful for diagnostics");
    }

    [Fact]
    public async Task With_no_environment_supplied_it_fails_closed()
    {
        // A caller that resolves the sender without an IHostEnvironment must get the safe
        // behaviour, not the verbatim one. Failing open here would silently reinstate the leak.
        var recorder = new AuthLogRecorder();
        var sender = new ConsoleEmailSender(Logger(recorder), environment: null);

        await sender.SendPasswordResetLinkAsync(User, Email, ResetLink);

        recorder.Entries.Should().NotBeEmpty();
        foreach (var entry in recorder.Entries)
        {
            foreach (var (name, text) in entry.Surfaces())
            {
                text.Should().NotContainEquivalentOf(Email, $"{name} must not carry the address");
                text.Should().NotContainEquivalentOf("SECRET-TOKEN-VALUE", $"{name} must not carry the link");
            }
        }
    }

    [Fact]
    public async Task In_development_the_link_stays_usable()
    {
        // The local workflow depends on copying the link out of the log, so this is a deliberate
        // exception rather than an oversight. Pinning it keeps a future tightening honest: if
        // someone removes it, they have to remove this test and explain why.
        var recorder = new AuthLogRecorder();
        var sender = new ConsoleEmailSender(
            Logger(recorder), new StubEnvironment("Development"));

        await sender.SendPasswordResetLinkAsync(User, Email, ResetLink);

        recorder.Entries.Should().Contain(e => e.Message.Contains("SECRET-TOKEN-VALUE"),
            "the development-only branch must still print a clickable link");
    }

    private static ILogger<ConsoleEmailSender> Logger(AuthLogRecorder recorder) =>
        LoggerFactory.Create(b =>
        {
            b.AddProvider(recorder);
            b.SetMinimumLevel(LogLevel.Trace);
        }).CreateLogger<ConsoleEmailSender>();

    private sealed class StubEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
