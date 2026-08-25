using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UnitTests.Services.Agents;

/// <summary>
/// Learning Coach Phase 0 guard for the preview-era Agent Framework cleanup.
///
/// The unused prototype was deleted: <c>ConversationAgentService</c>,
/// <c>IConversationAgentService</c>, <c>ConversationMemory</c>,
/// <c>VocabularyLookupTool</c>, the <c>AddConversationAgentServices</c>
/// registration helper (plus its MAUI and WebApp startup calls), and the
/// <c>Microsoft.Agents.AI.OpenAI</c> package reference on
/// <c>SentenceStudio.Shared</c>. Nothing ever consumed it — the path the
/// Conversation UI actually uses is <c>SentenceStudio.Services.ConversationService</c>,
/// registered by <c>AddSentenceStudioCoreServices</c>.
///
/// This file holds both halves of that guarantee, because asserting only one
/// half is what makes a dead-code cleanup dangerous:
///
/// <list type="number">
///   <item><b>Removal.</b> The dead types, their namespace, and the
///   <c>Microsoft.Agents.AI*</c> assembly dependency stay gone, and no project
///   re-introduces the removed symbols.</item>
///   <item><b>Survival.</b> The live <c>ConversationService</c> registration, the
///   Conversation page's dependency on it, and the persisted
///   <c>ConversationMemoryState</c> entity are NOT collateral damage. Deleting
///   the registration breaks the Conversation page at runtime with a DI
///   resolution failure, not a build error. Dropping the entity/table is
///   destructive and needs its own decision record plus a dual-provider
///   migration.</item>
/// </list>
///
/// The registration, page, and cross-project checks are source-level scans
/// because <c>CoreServiceExtensions</c> and the hosts live in net11.0 projects
/// (<c>SentenceStudio.AppLib</c> / <c>SentenceStudio.WebApp</c>) that this
/// net10.0 test project cannot reference, so the DI container cannot be built
/// here. Same convention as <c>PlanDateContextBannedSymbolsTests</c> and
/// <c>Concern2TimezoneRegressionTests</c>.
///
/// DO NOT DELETE OR WEAKEN THESE TESTS without an accompanying decision record.
/// </summary>
public class ConversationAgentRemovalTests
{
    private static readonly Assembly SharedAssembly = typeof(ApplicationDbContext).Assembly;

    /// <summary>
    /// Symbols whose reappearance in production source means the Phase 0
    /// cleanup regressed. <c>IConversationAgentService</c> is covered by the
    /// <c>ConversationAgentService</c> substring.
    ///
    /// The removed <c>ConversationMemory</c> type is deliberately NOT scanned for
    /// by name: it is a prefix of <c>ConversationMemoryState</c>, the entity this
    /// same file asserts is <i>retained</i>, so a substring scan would fail on the
    /// code we want to keep. Its removal is covered by the assembly-level type and
    /// namespace assertions above, which cannot collide.
    /// </summary>
    private static readonly string[] _removedSymbols =
    {
        "AddConversationAgentServices",
        "ConversationAgentService",
        "VocabularyLookupTool",
    };

    private static readonly string[] _scannedExtensions = { "*.cs", "*.razor" };

    // =====================================================================
    // 1. Removal — the dead path stays dead
    // =====================================================================

    [Theory]
    [InlineData("SentenceStudio.Services.Agents.IConversationAgentService")]
    [InlineData("SentenceStudio.Services.Agents.ConversationAgentService")]
    [InlineData("SentenceStudio.Services.Agents.ConversationMemory")]
    [InlineData("SentenceStudio.Services.Agents.VocabularyLookupTool")]
    public void RemovedAgentTypes_AreNotPresentInSharedAssembly(string fullTypeName)
    {
        SharedAssembly.GetType(fullTypeName)
            .Should().BeNull($"'{fullTypeName}' is dead preview-era code and must stay deleted");
    }

    [Fact]
    public void SharedAssembly_DeclaresNoTypesInTheRemovedAgentsNamespace()
    {
        var leftovers = SharedAssembly
            .GetTypes()
            .Where(t => t.Namespace == "SentenceStudio.Services.Agents")
            .Select(t => t.FullName)
            .ToList();

        leftovers.Should().BeEmpty("the SentenceStudio.Services.Agents namespace was removed entirely");
    }

    [Fact]
    public void SharedAssembly_DoesNotReferenceMicrosoftAgentsAi()
    {
        var referenced = SharedAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(name => name.StartsWith("Microsoft.Agents.AI", StringComparison.Ordinal))
            .ToList();

        referenced.Should().BeEmpty(
            "the preview Agent Framework packages were dropped from SentenceStudio.Shared; " +
            "Microsoft.Extensions.AI(.OpenAI) is now referenced explicitly instead");
    }

    [Fact]
    public void NoProductionSourceReferencesTheRemovedAgentSymbols()
    {
        // Scans all of src/ — production code, every host, and every UI project —
        // so it subsumes a UI-only scan. Comment lines are excluded on purpose:
        // an explanatory comment naming the removed service ("use ConversationService,
        // not ConversationAgentService") is the expected residue of a good cleanup,
        // not a regression. Only real code counts.
        var repoRoot = FindRepoRoot();
        var offenders = new List<string>();

        foreach (var file in EnumerateSourceFiles(Path.Combine(repoRoot, "src")))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (IsCommentLine(lines[i]))
                {
                    continue;
                }

                if (_removedSymbols.Any(symbol => lines[i].Contains(symbol, StringComparison.Ordinal)))
                {
                    offenders.Add($"{Path.GetRelativePath(repoRoot, file)}:{i + 1}");
                }
            }
        }

        offenders.Should().BeEmpty(
            "the preview-era agent path was deleted in Learning Coach Phase 0. Re-introducing " +
            "AddConversationAgentServices, ConversationAgentService/IConversationAgentService, " +
            "or VocabularyLookupTool means the cleanup regressed. Use ConversationService " +
            "(the live path) instead.\n" +
            string.Join("\n", offenders));
    }

    // =====================================================================
    // 2. Survival — the live path and persisted entity are not collateral damage
    // =====================================================================

    [Fact]
    public void CoreServiceRegistration_StillRegistersLiveConversationService()
    {
        var repoRoot = FindRepoRoot();
        var coreServiceExtensions = Path.Combine(
            repoRoot, "src", "SentenceStudio.AppLib", "Services", "CoreServiceExtensions.cs");

        File.Exists(coreServiceExtensions).Should().BeTrue(
            $"expected CoreServiceExtensions.cs at {coreServiceExtensions}; if it moved, update this guard");

        // Comment lines are stripped first: without that, a commented-out
        // registration still satisfies a naive substring match and the guard
        // silently stops guarding. Verified by mutation-testing this assertion.
        var codeLines = ReadCodeLines(coreServiceExtensions);

        codeLines.Should().Contain(
            line => line.Contains("AddSentenceStudioCoreServices", StringComparison.Ordinal),
            "the shared registration entry point must still exist");

        codeLines.Should().Contain(
            line => line.Contains("AddSingleton<ConversationService>()", StringComparison.Ordinal),
            "ConversationService is the live conversation path injected by " +
            "SentenceStudio.UI/Pages/Conversation.razor; the agent cleanup must not remove it " +
            "(that would be a runtime DI failure, not a build error). ConversationService is NOT " +
            "the deprecated agent path — that was ConversationAgentService/IConversationAgentService.");
    }

    [Fact]
    public void ConversationPage_StillDependsOnConversationService()
    {
        var repoRoot = FindRepoRoot();
        var conversationPage = Path.Combine(
            repoRoot, "src", "SentenceStudio.UI", "Pages", "Conversation.razor");

        File.Exists(conversationPage).Should().BeTrue(
            $"expected Conversation.razor at {conversationPage}; if it moved, update this guard");

        ReadCodeLines(conversationPage).Should().Contain(
            line => line.Contains("Inject", StringComparison.Ordinal)
                 && line.Contains("ConversationService", StringComparison.Ordinal),
            "Conversation.razor no longer injects ConversationService. This guard pins which " +
            "conversation service the UI actually consumes; if the page moved to a different " +
            "service, update both this test and the registration guard together.");
    }

    [Fact]
    public void ConversationMemoryState_EntityIsPreserved()
    {
        SharedAssembly.GetType("SentenceStudio.Shared.Models.ConversationMemoryState")
            .Should().NotBeNull("the entity is retained; dropping it is a separate destructive change");
    }

    [Fact]
    public void ConversationMemoryState_IsStillMappedOnApplicationDbContext()
    {
        var dbSet = typeof(ApplicationDbContext).GetProperty(
            nameof(ApplicationDbContext.ConversationMemoryStates),
            BindingFlags.Public | BindingFlags.Instance);

        dbSet.Should().NotBeNull("the ConversationMemoryState table mapping must survive the cleanup");
        dbSet!.PropertyType.Should().Be(typeof(DbSet<ConversationMemoryState>));
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static IEnumerable<string> EnumerateSourceFiles(string root)
    {
        foreach (var pattern in _scannedExtensions)
        {
            foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            {
                // Skip build output — bin/obj can contain generated copies of sources.
                var relative = Path.GetRelativePath(root, file);
                var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (segments.Any(s => s.Equals("bin", StringComparison.OrdinalIgnoreCase)
                                   || s.Equals("obj", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                yield return file;
            }
        }
    }

    private static IReadOnlyList<string> ReadCodeLines(string path)
        => File.ReadAllLines(path)
            .Where(line => !IsCommentLine(line))
            .ToList();

    private static bool IsCommentLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length == 0
            || trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("@*", StringComparison.Ordinal)   // razor comment
            || trimmed.StartsWith("*", StringComparison.Ordinal)    // xml/block comment body
            || trimmed.StartsWith("/*", StringComparison.Ordinal);
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
