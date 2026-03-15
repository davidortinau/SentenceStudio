using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

/// <summary>
/// Development email sender that writes email content to the console via ILogger.
/// Used when no SMTP server is configured.
/// </summary>
public class ConsoleEmailSender : IAppEmailSender
{
    private readonly ILogger<ConsoleEmailSender> _logger;

    public ConsoleEmailSender(ILogger<ConsoleEmailSender> logger)
    {
        _logger = logger;
    }

    public Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink)
    {
        var userName = user.DisplayName ?? user.UserName ?? email;
        var html = EmailTemplates.ConfirmEmail(userName, confirmationLink);

        _logger.LogInformation(
            "--- EMAIL: Confirmation ---\nTo: {Email}\nSubject: Confirm your SentenceStudio account\n\nLink: {Link}\n\n{Html}\n--- END EMAIL ---",
            email, confirmationLink, html);

        return Task.CompletedTask;
    }

    public Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink)
    {
        var userName = user.DisplayName ?? user.UserName ?? email;
        var html = EmailTemplates.ResetPassword(userName, resetLink);

        _logger.LogInformation(
            "--- EMAIL: Password Reset ---\nTo: {Email}\nSubject: Reset your SentenceStudio password\n\nLink: {Link}\n\n{Html}\n--- END EMAIL ---",
            email, resetLink, html);

        return Task.CompletedTask;
    }

    public Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode)
    {
        _logger.LogInformation(
            "--- EMAIL: Password Reset Code ---\nTo: {Email}\nCode: {Code}\n--- END EMAIL ---",
            email, resetCode);

        return Task.CompletedTask;
    }

    public Task SendEmailAsync(string toEmail, string subject, string htmlBody)
    {
        _logger.LogInformation(
            "--- EMAIL ---\nTo: {Email}\nSubject: {Subject}\n\n{Body}\n--- END EMAIL ---",
            toEmail, subject, htmlBody);

        return Task.CompletedTask;
    }
}
