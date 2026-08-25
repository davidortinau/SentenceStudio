#if !IOS && !ANDROID && !MACCATALYST && !MACOS
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Diagnostics;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

/// <summary>
/// Development email sender that writes email content to the console via ILogger.
/// Used when no SMTP server is configured.
/// </summary>
/// <remarks>
/// <para>
/// This type is registered unconditionally in both hosts, which means it also runs in the deployed
/// environment. Printing the message verbatim is the point in development — it is the stand-in for
/// opening a mail client — but the same code path in production wrote every learner's address,
/// display name, rendered body, and live confirmation/reset link into the structured log. A reset
/// link is a bearer credential: anyone with log access could take over an account with it.
/// </para>
/// <para>
/// So the verbatim dump is now gated on <c>IHostEnvironment.IsDevelopment()</c>.
/// Outside development the sender still records that a message was produced — the operationally
/// useful part — but only a masked recipient and bounded, non-secret facts.
/// </para>
/// </remarks>
public class ConsoleEmailSender : IAppEmailSender
{
    private readonly ILogger<ConsoleEmailSender> _logger;
    private readonly bool _verbatim;

    /// <param name="environment">
    /// When null the sender assumes it is <em>not</em> in development and redacts. Failing closed
    /// is the only safe default for a component whose unredacted mode emits credentials.
    /// </param>
    public ConsoleEmailSender(ILogger<ConsoleEmailSender> logger, IHostEnvironment? environment = null)
    {
        _logger = logger;
        _verbatim = environment?.IsDevelopment() ?? false;
    }

    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink)
    {
        if (_verbatim)
        {
            var userName = user.DisplayName ?? user.UserName ?? email;
            var html = EmailTemplates.ConfirmEmail(userName, confirmationLink);

            // allow:auth-log — development-only verbatim message dump, see type remarks
            _logger.LogInformation(
                "--- EMAIL: Confirmation ---\nTo: {Email}\nSubject: Confirm your SentenceStudio account\n\nLink: {Link}\n\n{Html}\n--- END EMAIL ---",
                email, confirmationLink, html);
        }
        else
        {
            _logger.LogInformation(
                "Sent confirmation email to {Email} for user {UserId}",
                AuthLogRedaction.MaskEmail(email), user.Id);
        }

        return Task.CompletedTask;
    }

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink)
    {
        if (_verbatim)
        {
            var userName = user.DisplayName ?? user.UserName ?? email;
            var html = EmailTemplates.ResetPassword(userName, resetLink);

            // allow:auth-log — development-only verbatim message dump, see type remarks
            _logger.LogInformation(
                "--- EMAIL: Password Reset ---\nTo: {Email}\nSubject: Reset your SentenceStudio password\n\nLink: {Link}\n\n{Html}\n--- END EMAIL ---",
                email, resetLink, html);
        }
        else
        {
            _logger.LogInformation(
                "Sent password reset email to {Email} for user {UserId}",
                AuthLogRedaction.MaskEmail(email), user.Id);
        }

        return Task.CompletedTask;
    }

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode)
    {
        if (_verbatim)
        {
            // allow:auth-log — development-only verbatim message dump, see type remarks
            _logger.LogInformation(
                "--- EMAIL: Password Reset Code ---\nTo: {Email}\nCode: {Code}\n--- END EMAIL ---",
                email, resetCode);
        }
        else
        {
            _logger.LogInformation(
                "Sent password reset code to {Email} for user {UserId}",
                AuthLogRedaction.MaskEmail(email), user.Id);
        }

        return Task.CompletedTask;
    }

    public Task SendEmailAsync(string toEmail, string subject, string htmlBody)
    {
        if (_verbatim)
        {
            // allow:auth-log — development-only verbatim message dump, see type remarks
            _logger.LogInformation(
                "--- EMAIL ---\nTo: {Email}\nSubject: {Subject}\n\n{Body}\n--- END EMAIL ---",
                toEmail, subject, htmlBody);
        }
        else
        {
            _logger.LogInformation(
                "Sent email to {Email} (subject length {SubjectLength}, body length {BodyLength})",
                AuthLogRedaction.MaskEmail(toEmail), subject?.Length ?? 0, htmlBody?.Length ?? 0);
        }

        return Task.CompletedTask;
    }
}
#endif
