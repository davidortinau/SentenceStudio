using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace SentenceStudio.UnitTests.Diagnostics;

/// <summary>
/// Source guard over the auth and account surface: no logger call may take a raw email address,
/// user name, display name, or <c>IdentityError.Description</c> as an argument.
/// </summary>
/// <remarks>
/// <para>
/// The runtime tests prove the call sites we know about are clean today. This proves the ones
/// written next week are clean too. The previous attempt at this fix masked four sites in one
/// file and left roughly sixteen others untouched, which is a failure a per-site test cannot
/// catch — there was no test for the sites nobody looked at.
/// </para>
/// <para>
/// A deliberate exception is spelled with <c>// allow:auth-log</c> and a reason on the same line.
/// There are three in the tree, all development-only, all noted at their call site.
/// </para>
/// </remarks>
public class AuthLogPiiGuardTests
{
    private const string AllowMarker = "// allow:auth-log";

    /// <summary>
    /// Every file in the auth and account surface. Directories are enumerated recursively; single
    /// files are listed explicitly so that a rename shows up as a failure rather than as silence.
    /// </summary>
    private static readonly string[] GatedDirectories =
    [
        Path.Combine("src", "SentenceStudio.Api", "Auth"),
        Path.Combine("src", "SentenceStudio.WebApp", "Auth"),
    ];

    private static readonly string[] GatedFiles =
    [
        Path.Combine("src", "SentenceStudio.Shared", "Services", "ConsoleEmailSender.cs"),
        Path.Combine("src", "SentenceStudio.Shared", "Services", "SmtpEmailSender.cs"),
        Path.Combine("src", "SentenceStudio.Shared", "Data", "DataRecoveryService.cs"),
        Path.Combine("src", "SentenceStudio.AppLib", "Services", "IdentityAuthService.cs"),
        Path.Combine("src", "SentenceStudio.UI", "Pages", "LoginPage.razor"),
        Path.Combine("src", "SentenceStudio.UI", "Pages", "RegisterPage.razor"),
        Path.Combine("src", "SentenceStudio.UI", "Pages", "Auth.razor"),
        Path.Combine("src", "SentenceStudio.UI", "Pages", "Profile.razor"),
    ];

    /// <summary>
    /// Arguments a logger must never receive. Each pattern matches an expression as it appears
    /// inside a <c>{Placeholder}</c>-style argument list, not the placeholder name — renaming the
    /// placeholder to <c>{MaskedEmail}</c> while still passing the raw value is the mistake this
    /// is looking for.
    /// </summary>
    private static readonly (Regex Pattern, string Reason)[] BannedArguments =
    [
        (new Regex(@"\brequest\.Email\b", RegexOptions.Compiled),
            "the address as submitted — mask with AuthLogRedaction.MaskEmail"),
        (new Regex(@"\b(?:user|appUser|existingUser|identityUser)\.Email\b", RegexOptions.Compiled),
            "the stored address — mask with AuthLogRedaction.MaskEmail, or log user.Id instead"),
        (new Regex(@"\b(?:user|appUser|existingUser|identityUser|result)\.UserName\b", RegexOptions.Compiled),
            "the user name, which is the address — mask with AuthLogRedaction.MaskUserName"),
        (new Regex(@"\b(?:userName|toEmail|recipientEmail)\b", RegexOptions.Compiled),
            "a raw address parameter — mask with AuthLogRedaction.MaskEmail/MaskUserName"),
        (new Regex(@"\bDisplayName\b", RegexOptions.Compiled),
            "a display name, which is usually a real name — use AuthLogRedaction.DescribeDisplayName"),
        (new Regex(@"\.Description\b", RegexOptions.Compiled),
            "an IdentityError description, which interpolates the offending address — "
            + "use AuthLogRedaction.DescribeIdentityErrors and log Code values"),
        (new Regex(@"\be\.Errors\b|\bresult\.Errors\b(?!\s*\.Select\s*\(\s*\w+\s*=>\s*\w+\.Code)", RegexOptions.Compiled),
            "an IdentityResult error collection whose ToString renders descriptions — "
            + "use AuthLogRedaction.DescribeIdentityErrors"),
    ];

    /// <summary>
    /// A logger invocation, including its full argument list across wrapped lines. Matches
    /// <c>_logger.LogInformation(...)</c>, <c>logger.LogWarning(...)</c>,
    /// <c>Logger.LogError(...)</c> and the <c>Log(LogLevel.X, ...)</c> form.
    /// </summary>
    private static readonly Regex LoggerCall = new(
        @"\b(?:_?[Ll]ogger|Log)\s*\.\s*(?:Log(?:Trace|Debug|Information|Warning|Error|Critical)?)\s*\(",
        RegexOptions.Compiled);

    /// <summary>
    /// A call into the redaction helper. Its arguments are removed before the banned-argument scan
    /// runs, because <c>MaskEmail(request.Email)</c> is the fix, not the defect — the helper is the
    /// one place allowed to touch the raw value.
    /// </summary>
    private static readonly Regex RedactionCall = new(
        @"\b\w*AuthLogRedaction\s*\.\s*\w+\s*\(",
        RegexOptions.Compiled);

    [Fact]
    public void No_auth_or_account_logger_call_receives_an_unmasked_identifier()
    {
        var repoRoot = FindRepoRoot();
        var files = ResolveGatedFiles(repoRoot);
        var offenders = new List<string>();

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            var relative = Path.GetRelativePath(repoRoot, file);

            foreach (var call in EnumerateLoggerCalls(source))
            {
                foreach (var (pattern, reason) in BannedArguments)
                {
                    var match = pattern.Match(call.Text);
                    if (!match.Success)
                    {
                        continue;
                    }

                    var line = source.Take(call.Start).Count(c => c == '\n') + 1;
                    offenders.Add(
                        $"{relative}:{line}: logger argument '{match.Value}' is {reason}. "
                        + $"If this is deliberate, append '{AllowMarker} <reason>' on the call.");
                }
            }
        }

        offenders.Should().BeEmpty(
            "auth and account logging must not carry personal identifiers:\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// The guard's own wiring. A path typo turns every assertion above into a test that passes
    /// because it examined nothing, which is worse than no guard at all — it reports safety.
    /// </summary>
    [Fact]
    public void The_guard_actually_reaches_the_files_it_claims_to_cover()
    {
        var repoRoot = FindRepoRoot();
        var files = ResolveGatedFiles(repoRoot);

        files.Should().HaveCountGreaterThan(10);

        // The three files the rejected fix touched, plus the two nobody had looked at.
        var required = new[]
        {
            "ServerAuthService.cs",
            "AccountEndpoints.cs",
            "AuthEndpoints.cs",
            "ConsoleEmailSender.cs",
            "IdentityAuthService.cs",
        };

        foreach (var name in required)
        {
            files.Should().Contain(
                f => Path.GetFileName(f) == name,
                "the guard must cover {0}", name);
        }
    }

    /// <summary>
    /// The detector's own detector. If <see cref="EnumerateLoggerCalls"/> or the pattern set stops
    /// matching — a refactor to a source generator, a logging-abstraction change — the main test
    /// goes quiet and stays green. This feeds it a known offender and requires a hit.
    /// </summary>
    [Fact]
    public void The_guard_still_recognises_a_violation()
    {
        const string offending = """
            public void Register(RegisterRequest request)
            {
                _logger.LogWarning(
                    "Registration failed for {Email}",
                    request.Email);
            }
            """;

        var calls = EnumerateLoggerCalls(offending).ToList();
        calls.Should().ContainSingle();

        BannedArguments
            .Any(b => b.Pattern.IsMatch(calls[0].Text))
            .Should().BeTrue("a raw request.Email argument must still be detected");
    }

    [Fact]
    public void The_guard_does_not_fire_on_a_masked_call()
    {
        const string clean = """
            public void Register(RegisterRequest request)
            {
                _logger.LogWarning(
                    "Registration failed for {Email} with {ErrorCodes}",
                    AuthLogRedaction.MaskEmail(request.Email),
                    AuthLogRedaction.DescribeIdentityErrors(result.Errors));
            }
            """;

        var calls = EnumerateLoggerCalls(clean).ToList();
        calls.Should().ContainSingle();

        var hits = BannedArguments
            .Where(b => b.Pattern.IsMatch(calls[0].Text))
            .Select(b => b.Reason)
            .ToList();

        hits.Should().BeEmpty("masked arguments are the shape we are asking for");
    }

    [Fact]
    public void An_allow_marker_above_the_call_suppresses_it()
    {
        // The reason for a deliberate exception is a sentence, and a sentence does not fit after a
        // comma inside an argument list — so the marker is written above the call.
        const string marked = """
            public void Send(string toEmail)
            {
                // allow:auth-log — development-only verbatim dump
                _logger.LogInformation("To: {Email}", toEmail);
            }
            """;

        EnumerateLoggerCalls(marked).Should().BeEmpty();
    }

    [Fact]
    public void A_comment_mentioning_an_identifier_is_not_a_violation()
    {
        // Comments explaining *why* a value is masked necessarily name the value. Flagging them
        // would push authors to delete the explanation, which is the opposite of the goal.
        const string commented = """
            public void Register(RegisterRequest request)
            {
                // request.Email is deliberately masked here; see AuthLogRedaction.
                _logger.LogWarning("Registration failed for {Email}", masked);
            }
            """;

        var calls = EnumerateLoggerCalls(commented).ToList();
        calls.Should().ContainSingle();
        BannedArguments.Any(b => b.Pattern.IsMatch(calls[0].Text)).Should().BeFalse();
    }

    /// <summary>
    /// Yields each logger invocation's argument list, brace-balanced so that a call wrapped over
    /// five lines is examined as one unit, with comments stripped so that prose about a field is
    /// not mistaken for a use of it, and with redaction-helper arguments removed so that the fix
    /// is not reported as the defect.
    /// </summary>
    private static IEnumerable<(int Start, string Text)> EnumerateLoggerCalls(string source)
    {
        var stripped = StripComments(source);

        foreach (Match match in LoggerCall.Matches(stripped))
        {
            var open = match.Index + match.Length - 1;
            var end = FindMatchingParen(stripped, open);
            if (end <= open)
            {
                continue;
            }

            if (IsAllowed(source, match.Index, end))
            {
                continue;
            }

            yield return (match.Index, StripRedactionCalls(stripped[open..(end + 1)]));
        }
    }

    /// <summary>
    /// True when an <c>// allow:auth-log</c> marker governs this call. The marker is accepted
    /// anywhere in the call itself or on the three lines above it, because the reason for a
    /// deliberate exception is usually a sentence, and a sentence does not fit after a comma
    /// inside an argument list.
    /// </summary>
    private static bool IsAllowed(string source, int callStart, int callEnd)
    {
        var scanFrom = callStart;
        for (var lines = 0; lines < 4 && scanFrom > 0; lines++)
        {
            var previous = source.LastIndexOf('\n', Math.Max(scanFrom - 1, 0));
            if (previous < 0)
            {
                scanFrom = 0;
                break;
            }

            scanFrom = previous;
        }

        var window = source[scanFrom..Math.Min(callEnd + 1, source.Length)];
        return window.Contains(AllowMarker, StringComparison.Ordinal);
    }

    /// <summary>
    /// Blanks out the arguments of every <c>AuthLogRedaction.*</c> call. Length is preserved so
    /// that reported offsets stay true.
    /// </summary>
    private static string StripRedactionCalls(string text)
    {
        var buffer = text.ToCharArray();

        foreach (Match match in RedactionCall.Matches(text))
        {
            var open = match.Index + match.Length - 1;
            var end = FindMatchingParen(text, open);
            if (end <= open)
            {
                continue;
            }

            for (var i = match.Index; i <= end && i < buffer.Length; i++)
            {
                if (buffer[i] != '\n')
                {
                    buffer[i] = ' ';
                }
            }
        }

        return new string(buffer);
    }

    private static int FindMatchingParen(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '(')
            {
                depth++;
            }
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// Blanks out line and block comments and string literals, preserving length and newlines so
    /// reported line numbers stay true. String literals are blanked because a log *template* names
    /// its placeholders — <c>"failed for {Email}"</c> is not a use of an email.
    /// </summary>
    private static string StripComments(string source)
    {
        var buffer = source.ToCharArray();

        for (var i = 0; i < buffer.Length; i++)
        {
            if (buffer[i] == '/' && i + 1 < buffer.Length && buffer[i + 1] == '/')
            {
                while (i < buffer.Length && buffer[i] != '\n')
                {
                    buffer[i++] = ' ';
                }
            }
            else if (buffer[i] == '/' && i + 1 < buffer.Length && buffer[i + 1] == '*')
            {
                while (i < buffer.Length
                       && !(buffer[i] == '*' && i + 1 < buffer.Length && buffer[i + 1] == '/'))
                {
                    if (buffer[i] != '\n')
                    {
                        buffer[i] = ' ';
                    }

                    i++;
                }

                if (i + 1 < buffer.Length)
                {
                    buffer[i] = ' ';
                    buffer[i + 1] = ' ';
                    i++;
                }
            }
            else if (buffer[i] == '"')
            {
                var j = i + 1;
                while (j < buffer.Length && buffer[j] != '"' && buffer[j] != '\n')
                {
                    if (buffer[j] == '\\')
                    {
                        j++;
                    }

                    j++;
                }

                for (var k = i + 1; k < j && k < buffer.Length; k++)
                {
                    if (buffer[k] != '\n')
                    {
                        buffer[k] = ' ';
                    }
                }

                i = j;
            }
        }

        return new string(buffer);
    }

    private static IReadOnlyList<string> ResolveGatedFiles(string repoRoot)
    {
        var files = new List<string>();

        foreach (var relative in GatedDirectories)
        {
            var directory = Path.Combine(repoRoot, relative);

            // Fail loudly. A guard that skips a directory it cannot find is a guard that reports
            // "no violations" for code it never opened.
            Directory.Exists(directory).Should().BeTrue(
                "gated directory '{0}' must exist — if it moved, update GatedDirectories", relative);

            files.AddRange(Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories));
            files.AddRange(Directory.EnumerateFiles(directory, "*.razor", SearchOption.AllDirectories));
        }

        foreach (var relative in GatedFiles)
        {
            var file = Path.Combine(repoRoot, relative);
            File.Exists(file).Should().BeTrue(
                "gated file '{0}' must exist — if it moved, update GatedFiles", relative);

            files.Add(file);
        }

        return files;
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "SentenceStudio.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate repo root (expected src/SentenceStudio.sln).");
    }
}
