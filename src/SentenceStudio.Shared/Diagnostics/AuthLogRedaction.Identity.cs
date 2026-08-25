// ASP.NET Core Identity is a server-only dependency in this project (see the FrameworkReference
// condition in SentenceStudio.Shared.csproj), so the IdentityError-typed convenience lives here
// behind the same guard the email senders use rather than in the platform-neutral file.
#if !IOS && !ANDROID && !MACCATALYST && !MACOS

using Microsoft.AspNetCore.Identity;

namespace SentenceStudio.Shared.Diagnostics;

public static partial class AuthLogRedaction
{
    /// <summary>
    /// Renders an Identity result's error <b>codes</b>, never its descriptions.
    /// </summary>
    /// <remarks>
    /// This overload exists so a call site never has to write <c>.Select(e =&gt; e.Description)</c>
    /// to get something loggable. The descriptions are the leak: <c>DuplicateEmail</c> renders as
    /// <c>"Email 'someone@example.com' is already taken."</c>, and <c>DuplicateUserName</c> the
    /// same with the user name. The codes keep the diagnostic — which rule failed — without the
    /// value that failed it.
    /// </remarks>
    public static string DescribeIdentityErrors(IEnumerable<IdentityError>? errors) =>
        DescribeErrorCodes(errors?.Select(e => e.Code));
}

#endif
