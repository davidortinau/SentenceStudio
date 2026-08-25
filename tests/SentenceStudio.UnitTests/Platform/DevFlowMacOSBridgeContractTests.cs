using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace SentenceStudio.UnitTests.Platform;

/// <summary>
/// Regression + source-contract tests for the DEBUG-only DevFlow macOS bridge
/// (<c>src/SentenceStudio.MacOS/DevFlowMacOSBridge.cs</c>).
///
/// WHY THIS EXISTS
/// ---------------
/// On 2026-08-18 the macOS (AppKit) head reported a healthy launch while
/// <c>maui devflow agent status</c> returned "Cannot connect to agent at localhost:9225" and the
/// broker showed <c>agent_count=0</c>. Root cause: <c>AddMauiDevFlowAgent</c> starts the agent with
/// <c>app.Dispatcher.Dispatch(() =&gt; service.Start(...))</c> from a handler that is ALREADY on the
/// AppKit main thread, then immediately prints "Agent started on port N". The app blocked the main
/// thread right afterwards in a Keychain read (SecureStorage, behind a SecurityAgent prompt that
/// reappears after every rebuild because the ad-hoc signature changes), so the run loop never
/// turned, the queued Start never executed, and no listener was ever opened — while the console
/// claimed success.
///
/// The workaround starts the already-registered singleton synchronously. These tests make sure it
/// cannot silently rot:
///   * the source contract catches anyone deleting the bridge call or reintroducing a dispatch;
///   * the metadata contract catches a DevFlow package bump that renames/removes the members the
///     bridge calls (which would otherwise only fail on a macOS Debug build).
///
/// This test project targets net10.0 and cannot reference the macOS head, so the bridge is verified
/// by source text plus reflection-metadata inspection of the DevFlow assembly — never by loading it
/// (loading would drag in the MAUI workload assemblies).
/// </summary>
public class DevFlowMacOSBridgeContractTests
{
    private const string BridgeRelativePath = "src/SentenceStudio.MacOS/DevFlowMacOSBridge.cs";
    private const string AppDelegateRelativePath = "src/SentenceStudio.MacOS/MauiMacOSApp.cs";

    [Fact]
    public void Bridge_file_exists_and_is_debug_only()
    {
        var source = ReadRepoFile(BridgeRelativePath);

        Assert.StartsWith("#if DEBUG", source.TrimStart());
        Assert.EndsWith("#endif", source.TrimEnd());

        // Release builds must not reference DevFlow at all — the package references in the head
        // project are Debug-conditional, so a non-guarded using would break the Release build.
        Assert.Contains("using Microsoft.Maui.DevFlow.Agent.Core;", source);
    }

    [Fact]
    public void Bridge_starts_the_agent_synchronously_not_through_the_dispatcher()
    {
        var source = ReadRepoFile(BridgeRelativePath);
        var codeOnly = StripComments(source);

        // Required calls — checked against code-only source so commenting them out causes failure.
        Assert.Contains("service.Start(app, dispatcher);", codeOnly);
        Assert.Contains("service.StartServerOnly(dispatcher);", codeOnly);

        // The entire point of the workaround: no Dispatcher round-trip. Reintroducing
        // Dispatch/BeginInvokeOnMainThread here recreates the original bug, where a main thread that
        // blocks before the next run-loop turn permanently swallows the agent start.
        var offenders = Regex.Matches(codeOnly, @"\.Dispatch\s*\(|\.DispatchAsync\s*\(|BeginInvokeOnMainThread\s*\(")
            .Select(m => m.Value)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "DevFlowMacOSBridge must start the DevFlow agent synchronously on the AppKit main "
            + "thread. Found deferred-execution calls: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Bridge_reuses_the_registered_singleton_and_never_re_registers_with_the_broker()
    {
        var source = ReadRepoFile(BridgeRelativePath);
        var codeOnly = StripComments(source);

        // Must resolve the existing singleton — checked against code-only source.
        Assert.Contains("GetService<DevFlowAgentService>()", codeOnly);

        // ...and never construct a second service or a second broker registration, which would
        // produce a duplicate agent entry in the broker and a second listener.
        Assert.DoesNotContain("new DevFlowAgentService", codeOnly);
        Assert.DoesNotContain("new BrokerRegistration", codeOnly);
        Assert.DoesNotContain("SetBrokerRegistration", codeOnly);
    }

    [Fact]
    public void AppDelegate_invokes_the_bridge_after_base_DidFinishLaunching()
    {
        var source = ReadRepoFile(AppDelegateRelativePath);
        var codeOnly = StripComments(source);

        var baseCall = codeOnly.IndexOf("base.DidFinishLaunching(notification);", StringComparison.Ordinal);
        var bridgeCall = codeOnly.IndexOf("DevFlowMacOSBridge.StartAgentIfNeeded(Services);", StringComparison.Ordinal);

        Assert.True(baseCall >= 0, $"{AppDelegateRelativePath} must call base.DidFinishLaunching.");
        Assert.True(
            bridgeCall >= 0,
            $"{AppDelegateRelativePath} must call DevFlowMacOSBridge.StartAgentIfNeeded(Services); "
            + "without it the DevFlow agent never listens on the macOS head.");

        // Application.Current and the MauiApp only exist after the base implementation returns.
        Assert.True(
            bridgeCall > baseCall,
            "DevFlowMacOSBridge.StartAgentIfNeeded must run AFTER base.DidFinishLaunching, "
            + "otherwise Application.Current is null and the agent cannot bind the app.");

        Assert.Contains("#if DEBUG", source);
    }

    [Fact]
    public void DevFlow_agent_assembly_still_exposes_the_members_the_bridge_calls()
    {
        var assemblyPath = TryFindDevFlowAgentCoreAssembly();
        if (assemblyPath is null)
        {
            // TryFind returns null only when the entire package family directory is absent (CI
            // without the DevFlow restore). If the family exists but the resolved version is
            // missing, TryFind fails loudly instead of returning null.
            return;
        }

        var members = ReadPublicMembers(assemblyPath, "Microsoft.Maui.DevFlow.Agent.Core", "DevFlowAgentService");

        Assert.True(members.Count > 0, $"DevFlowAgentService not found in {assemblyPath}.");

        foreach (var required in new[] { "Start", "StartServerOnly", "BindApp", "get_IsRunning", "get_IsAppBound", "get_Port" })
        {
            Assert.True(
                members.Contains(required),
                $"DevFlowAgentService.{required} is gone from {Path.GetFileName(assemblyPath)}. "
                + "src/SentenceStudio.MacOS/DevFlowMacOSBridge.cs depends on it — re-check whether the "
                + "upstream agent now starts itself without a dispatcher round-trip, and delete the "
                + "bridge if so.");
        }
    }

    // ---- comment-stripping self-tests -------------------------------------------------

    [Theory]
    [InlineData("var x = 1; // service.Start(app, dispatcher);\nservice.Start(app, dispatcher);",
        true, "Line comment should be stripped; executable call preserved")]
    [InlineData("/* service.Start(app, dispatcher); */\nservice.Start(app, dispatcher);",
        true, "Block comment should be stripped; executable call preserved")]
    [InlineData("// service.Start(app, dispatcher);\n// more comments",
        false, "All code is commented out — call must NOT be found")]
    [InlineData("var url = \"https://example.com/path\"; // comment\nservice.Start(app, dispatcher);",
        true, "URL in string literal must survive; call preserved")]
    [InlineData("var s = \"not a // comment\"; service.Start(app, dispatcher);",
        true, "Double-slash inside string literal is not a comment")]
    public void StripComments_handles_edge_cases(string input, bool shouldContainCall, string because)
    {
        var stripped = StripComments(input);
        if (shouldContainCall)
            Assert.Contains("service.Start(app, dispatcher);", stripped);
        else
            Assert.DoesNotContain("service.Start(app, dispatcher);", stripped);
    }

    // ---- helpers -----------------------------------------------------------------------

    private static string ReadRepoFile(string relativePath)
    {
        var full = Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(full), $"Expected file not found: {relativePath}");
        return File.ReadAllText(full);
    }

    /// <summary>
    /// Reads public member names of a type using metadata only. Loading the assembly would require
    /// the MAUI workload assemblies (Microsoft.Maui.Controls et al.) that this net10.0 test project
    /// deliberately does not reference.
    /// </summary>
    private static HashSet<string> ReadPublicMembers(string assemblyPath, string namespaceName, string typeName)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);

        using var stream = File.OpenRead(assemblyPath);
        using var pe = new PEReader(stream);
        var reader = pe.GetMetadataReader();

        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (!reader.GetString(type.Name).Equals(typeName, StringComparison.Ordinal))
                continue;
            if (!reader.GetString(type.Namespace).Equals(namespaceName, StringComparison.Ordinal))
                continue;

            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if ((method.Attributes & MethodAttributes.MemberAccessMask) == MethodAttributes.Public)
                    result.Add(reader.GetString(method.Name));
            }
        }

        return result;
    }

    /// <summary>
    /// Locates the DevFlow Agent.Core assembly for metadata inspection.
    ///
    /// Resolution order:
    ///  1. Read the actual resolved version from the macOS head's <c>project.assets.json</c> — this
    ///     is the ground truth for what <c>dotnet restore</c> selected, accounting for version
    ///     ranges, floating versions, and CPM overrides.
    ///  2. Fall back to the CPM pin in <c>Directory.Packages.props</c> when the assets file is
    ///     absent (CI legs that only restore the UnitTests project).
    ///
    /// Skip vs. fail logic:
    ///  * If the entire <c>microsoft.maui.devflow.agent.core</c> package family directory is absent
    ///    from the NuGet cache, the DevFlow packages were never restored on this machine — skip
    ///    (return null). This is the normal CI state for a test-only restore.
    ///  * If the family directory exists but the resolved/pinned version subdirectory is missing,
    ///    fail loudly — someone restored DevFlow but the version the project expects isn't there.
    /// </summary>
    private static string? TryFindDevFlowAgentCoreAssembly()
    {
        var packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages");

        var packageFamilyDir = Path.Combine(packagesRoot, "microsoft.maui.devflow.agent.core");

        // If the entire package family directory is absent, DevFlow was never restored — skip.
        if (!Directory.Exists(packageFamilyDir))
            return null;

        // Family exists — resolve the expected version.
        var resolvedVersion = TryReadResolvedAgentCoreVersion()
            ?? ReadPinnedAgentCoreVersion();

        if (resolvedVersion is null)
        {
            Assert.Fail(
                "microsoft.maui.devflow.agent.core is cached locally but neither "
                + "project.assets.json nor Directory.Packages.props specifies a version. "
                + "Run 'dotnet restore' on the macOS head.");
            return null; // unreachable
        }

        var versionDir = Path.Combine(packageFamilyDir, resolvedVersion);
        if (!Directory.Exists(versionDir))
        {
            // Family present, resolved version missing — fail loud (wrong-version protection).
            Assert.Fail(
                $"Microsoft.Maui.DevFlow.Agent.Core {resolvedVersion} "
                + $"is not restored at '{versionDir}'. Available versions: "
                + string.Join(", ", Directory.GetDirectories(packageFamilyDir).Select(Path.GetFileName))
                + ". Run 'dotnet restore' on the macOS head to pull the correct version.");
            return null; // unreachable
        }

        return Directory.EnumerateFiles(versionDir, "Microsoft.Maui.DevFlow.Agent.Core.dll", SearchOption.AllDirectories)
            .FirstOrDefault()
            ?? throw new InvalidOperationException(
                $"Package directory '{versionDir}' exists but contains no Agent.Core DLL. "
                + "Run 'dotnet restore' on the macOS head to populate the package.");
    }

    /// <summary>
    /// Reads the resolved Agent.Core version from the macOS head's <c>project.assets.json</c>.
    /// This is the ground truth — it reflects what NuGet actually resolved, not just what CPM pins.
    /// Returns null when the assets file doesn't exist (e.g. CI with no macOS restore).
    /// </summary>
    private static string? TryReadResolvedAgentCoreVersion()
    {
        var assetsPath = Path.Combine(FindRepoRoot(), "src", "SentenceStudio.MacOS", "obj", "project.assets.json");
        if (!File.Exists(assetsPath))
            return null;

        try
        {
            using var stream = File.OpenRead(assetsPath);
            using var doc = JsonDocument.Parse(stream);

            if (!doc.RootElement.TryGetProperty("libraries", out var libraries))
                return null;

            // Library keys are "PackageId/Version"
            foreach (var lib in libraries.EnumerateObject())
            {
                if (lib.Name.StartsWith("Microsoft.Maui.DevFlow.Agent.Core/", StringComparison.OrdinalIgnoreCase))
                    return lib.Name.Substring("Microsoft.Maui.DevFlow.Agent.Core/".Length);
            }
        }
        catch
        {
            // Malformed assets file — fall through to CPM.
        }

        return null;
    }

    /// <summary>
    /// Reads the CPM-pinned version of Microsoft.Maui.DevFlow.Agent from Directory.Packages.props.
    /// The Agent.Core transitive package uses the same version band; if not listed separately, the
    /// parent Agent version is used. Used as fallback when project.assets.json is absent.
    /// </summary>
    private static string? ReadPinnedAgentCoreVersion()
    {
        var propsPath = Path.Combine(FindRepoRoot(), "Directory.Packages.props");
        if (!File.Exists(propsPath))
            return null;

        var doc = XDocument.Load(propsPath);
        // Look for Agent.Core first, fall back to Agent (they share the same version).
        foreach (var packageId in new[] { "Microsoft.Maui.DevFlow.Agent.Core", "Microsoft.Maui.DevFlow.Agent" })
        {
            var version = doc.Descendants("PackageVersion")
                .FirstOrDefault(e => string.Equals(e.Attribute("Include")?.Value, packageId, StringComparison.OrdinalIgnoreCase))
                ?.Attribute("Version")?.Value;
            if (version is not null)
                return version;
        }

        return null;
    }

    /// <summary>
    /// Lexical comment stripper that handles C# single-line (<c>//</c>) and multi-line
    /// (<c>/* ... */</c>) comments while respecting string literals (regular, verbatim, and raw).
    ///
    /// This is a state-machine scanner (no regex, no Roslyn dependency) that avoids false negatives
    /// on URLs inside strings (e.g. <c>"https://example.com"</c>) and false positives on
    /// doc-comment examples mentioning forbidden API names.
    /// </summary>
    internal static string StripComments(string source)
    {
        var sb = new StringBuilder(source.Length);
        int i = 0;
        int len = source.Length;

        while (i < len)
        {
            char c = source[i];

            // String literal — consume whole literal, preserving content.
            if (c == '"')
            {
                // Check for verbatim string @"..."
                if (i > 0 && source[i - 1] == '@')
                {
                    sb.Append(c); i++;
                    while (i < len)
                    {
                        if (source[i] == '"')
                        {
                            sb.Append('"');
                            i++;
                            if (i < len && source[i] == '"')
                            {
                                sb.Append('"'); i++; // escaped ""
                            }
                            else break;
                        }
                        else { sb.Append(source[i]); i++; }
                    }
                    continue;
                }
                // Regular string "..."
                sb.Append(c); i++;
                while (i < len && source[i] != '"')
                {
                    if (source[i] == '\\' && i + 1 < len)
                    {
                        sb.Append(source[i]); sb.Append(source[i + 1]); i += 2;
                    }
                    else { sb.Append(source[i]); i++; }
                }
                if (i < len) { sb.Append(source[i]); i++; } // closing "
                continue;
            }

            // Character literal '...'
            if (c == '\'')
            {
                sb.Append(c); i++;
                while (i < len && source[i] != '\'')
                {
                    if (source[i] == '\\' && i + 1 < len)
                    {
                        sb.Append(source[i]); sb.Append(source[i + 1]); i += 2;
                    }
                    else { sb.Append(source[i]); i++; }
                }
                if (i < len) { sb.Append(source[i]); i++; }
                continue;
            }

            // Potential comment
            if (c == '/' && i + 1 < len)
            {
                if (source[i + 1] == '/')
                {
                    // Single-line comment — skip to end of line, replace with space.
                    sb.Append(' ');
                    i += 2;
                    while (i < len && source[i] != '\n' && source[i] != '\r') i++;
                    continue;
                }
                if (source[i + 1] == '*')
                {
                    // Block comment — skip to */, replace with space.
                    sb.Append(' ');
                    i += 2;
                    while (i < len)
                    {
                        if (source[i] == '*' && i + 1 < len && source[i + 1] == '/')
                        {
                            i += 2; break;
                        }
                        i++;
                    }
                    continue;
                }
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src"))
                && File.Exists(Path.Combine(dir.FullName, "src", "SentenceStudio.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate repo root (expected src/SentenceStudio.sln).");
    }
}
