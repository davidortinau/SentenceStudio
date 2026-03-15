using System.Net;
using System.Net.Mail;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

/// <summary>
/// Production email sender using System.Net.Mail.SmtpClient.
/// Reads SMTP configuration from the Email: section in IConfiguration.
/// </summary>
public class SmtpEmailSender : IAppEmailSender
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(IConfiguration configuration, ILogger<SmtpEmailSender> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendConfirmationLinkAsync(ApplicationUser user, string email, string confirmationLink)
    {
        var userName = user.DisplayName ?? user.UserName ?? email;
        var html = EmailTemplates.ConfirmEmail(userName, confirmationLink);
        await SendEmailAsync(email, "Confirm your SentenceStudio account", html);
    }

    public async Task SendPasswordResetLinkAsync(ApplicationUser user, string email, string resetLink)
    {
        var userName = user.DisplayName ?? user.UserName ?? email;
        var html = EmailTemplates.ResetPassword(userName, resetLink);
        await SendEmailAsync(email, "Reset your SentenceStudio password", html);
    }

    public async Task SendPasswordResetCodeAsync(ApplicationUser user, string email, string resetCode)
    {
        var html = $"<p>Your password reset code is: <strong>{resetCode}</strong></p>";
        await SendEmailAsync(email, "Your SentenceStudio password reset code", html);
    }

    public async Task SendEmailAsync(string toEmail, string subject, string htmlBody)
    {
        var smtpHost = _configuration["Email:SmtpHost"]
            ?? throw new InvalidOperationException("Email:SmtpHost is not configured.");
        var smtpPort = int.Parse(_configuration["Email:SmtpPort"] ?? "587");
        var fromAddress = _configuration["Email:FromAddress"]
            ?? throw new InvalidOperationException("Email:FromAddress is not configured.");
        var fromName = _configuration["Email:FromName"] ?? "SentenceStudio";
        var username = _configuration["Email:Username"];
        var password = _configuration["Email:Password"];

        using var message = new MailMessage();
        message.From = new MailAddress(fromAddress, fromName);
        message.To.Add(new MailAddress(toEmail));
        message.Subject = subject;
        message.Body = htmlBody;
        message.IsBodyHtml = true;

        using var client = new SmtpClient(smtpHost, smtpPort);
        client.EnableSsl = true;

        if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
        {
            client.Credentials = new NetworkCredential(username, password);
        }

        try
        {
            await client.SendMailAsync(message);
            _logger.LogInformation("Email sent to {Email}: {Subject}", toEmail, subject);
        }
        catch (SmtpException ex)
        {
            _logger.LogError(ex, "Failed to send email to {Email}: {Subject}", toEmail, subject);
            throw;
        }
    }
}
