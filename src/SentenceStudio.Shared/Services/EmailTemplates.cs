namespace SentenceStudio.Services;

/// <summary>
/// Simple HTML email templates for Identity workflows.
/// </summary>
public static class EmailTemplates
{
    /// <summary>
    /// Returns an HTML email body for account confirmation.
    /// </summary>
    public static string ConfirmEmail(string userName, string confirmUrl)
    {
        return $"""
            <!DOCTYPE html>
            <html>
            <head><meta charset="utf-8" /></head>
            <body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; color: #333; max-width: 600px; margin: 0 auto; padding: 20px;">
                <h2 style="color: #1a1a2e;">Welcome to SentenceStudio</h2>
                <p>Hi {userName},</p>
                <p>Thanks for creating your SentenceStudio account. Please confirm your email address by clicking the link below:</p>
                <p style="margin: 24px 0;">
                    <a href="{confirmUrl}"
                       style="background-color: #1a1a2e; color: #ffffff; padding: 12px 24px; text-decoration: none; border-radius: 6px; display: inline-block;">
                        Confirm Email Address
                    </a>
                </p>
                <p>If the button above does not work, copy and paste this URL into your browser:</p>
                <p style="word-break: break-all; color: #666; font-size: 14px;">{confirmUrl}</p>
                <hr style="border: none; border-top: 1px solid #eee; margin: 24px 0;" />
                <p style="font-size: 12px; color: #999;">
                    If you did not create a SentenceStudio account, you can safely ignore this email.
                </p>
            </body>
            </html>
            """;
    }

    /// <summary>
    /// Returns an HTML email body for password reset.
    /// </summary>
    public static string ResetPassword(string userName, string resetUrl)
    {
        return $"""
            <!DOCTYPE html>
            <html>
            <head><meta charset="utf-8" /></head>
            <body style="font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; color: #333; max-width: 600px; margin: 0 auto; padding: 20px;">
                <h2 style="color: #1a1a2e;">SentenceStudio Password Reset</h2>
                <p>Hi {userName},</p>
                <p>We received a request to reset the password for your SentenceStudio account. Click the link below to choose a new password:</p>
                <p style="margin: 24px 0;">
                    <a href="{resetUrl}"
                       style="background-color: #1a1a2e; color: #ffffff; padding: 12px 24px; text-decoration: none; border-radius: 6px; display: inline-block;">
                        Reset Password
                    </a>
                </p>
                <p>If the button above does not work, copy and paste this URL into your browser:</p>
                <p style="word-break: break-all; color: #666; font-size: 14px;">{resetUrl}</p>
                <hr style="border: none; border-top: 1px solid #eee; margin: 24px 0;" />
                <p style="font-size: 12px; color: #999;">
                    If you did not request a password reset, you can safely ignore this email. Your password will not be changed.
                </p>
            </body>
            </html>
            """;
    }
}
