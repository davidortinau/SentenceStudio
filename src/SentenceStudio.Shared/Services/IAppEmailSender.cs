using Microsoft.AspNetCore.Identity;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Services;

/// <summary>
/// Application email sender that extends Identity's IEmailSender.
/// Provides a general-purpose SendEmailAsync alongside the Identity-required methods.
/// </summary>
public interface IAppEmailSender : IEmailSender<ApplicationUser>
{
    /// <summary>
    /// Send an arbitrary email (used outside the Identity flow).
    /// </summary>
    Task SendEmailAsync(string toEmail, string subject, string htmlBody);
}
