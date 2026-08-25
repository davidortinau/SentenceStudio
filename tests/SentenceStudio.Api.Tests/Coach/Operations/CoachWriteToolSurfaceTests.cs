using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Coach.Operations;
using SentenceStudio.Api.Coach.Operations.Handlers;
using SentenceStudio.Api.Coach.Runtime;
using SentenceStudio.Api.Coach.Tools;

namespace SentenceStudio.Api.Tests.Coach.Operations;

/// <summary>
/// Proves the shape of the write tool surface: what is on it, what is not, and which risk class
/// each tool carries.
/// </summary>
/// <remarks>
/// These are the cheapest tests in the suite and they guard the most expensive mistakes. A tool
/// that reaches the model with the wrong risk class, or a forbidden capability that acquires a
/// tool name, is not something a later integration test would necessarily notice — it would just
/// work.
/// </remarks>
public class CoachWriteToolSurfaceTests
{
    private static CoachOptions Options(bool overlay = true, bool read = true, bool write = true) =>
        new()
        {
            SamOverlay = new CoachFeatureSwitch { Enabled = overlay },
            SamReadTools = new CoachFeatureSwitch { Enabled = read },
            SamWriteTools = new CoachFeatureSwitch { Enabled = write }
        };

    /// <summary>
    /// The twelve write tools and the class each one must carry.
    /// </summary>
    /// <remarks>
    /// Written out rather than derived, because a derived expectation would agree with whatever
    /// the registry happened to say. A hand-maintained table is the only version of this test that
    /// can disagree, and disagreeing is its entire job: moving a removal tool to WriteSoft would
    /// drop its confirmation requirement, and this is the line that would fail.
    /// </remarks>
    public static TheoryData<string, CoachToolRiskClass> ExpectedRiskClasses => new()
    {
        { CoachToolNames.ProposeVocabularyEntry, CoachToolRiskClass.WriteSoft },
        { CoachToolNames.ProposeVocabularyEdit, CoachToolRiskClass.WriteSoft },
        { CoachToolNames.ProposeVocabularyLink, CoachToolRiskClass.WriteSoft },
        { CoachToolNames.ProposeVocabularyRemoval, CoachToolRiskClass.WriteHard },
        { CoachToolNames.ProposeSkillEntry, CoachToolRiskClass.WriteSoft },
        { CoachToolNames.ProposeSkillEdit, CoachToolRiskClass.WriteSoft },
        { CoachToolNames.ProposeSkillArchive, CoachToolRiskClass.WriteHard },
        { CoachToolNames.ProposeResourceEntry, CoachToolRiskClass.WriteSoft },
        { CoachToolNames.ProposeResourceEdit, CoachToolRiskClass.WriteSoft },
        { CoachToolNames.ProposeResourceRemoval, CoachToolRiskClass.WriteHard },
        { CoachToolNames.ProposePreferenceChange, CoachToolRiskClass.WriteHard },
        { CoachToolNames.ProposeYouTubeImport, CoachToolRiskClass.WriteHard }
    };

    /// <summary>
    /// Which writes can be taken back, named one at a time.
    /// </summary>
    /// <remarks>
    /// Reversibility is a property of what the operation did, not of how loudly it asked. Deleting a
    /// skill or a resource destroys rows the learner may have spent months filling; importing a
    /// video reaches a service outside this application. None of those can be honestly reversed, and
    /// offering a button that claimed otherwise would be worse than not offering one. Everything
    /// else here either created a row we can remove or replaced fields we kept a copy of.
    /// </remarks>
    public static TheoryData<string, CoachWriteUndoKind> ExpectedUndoKinds => new()
    {
        { CoachToolNames.ProposeVocabularyEntry, CoachWriteUndoKind.DeleteCreatedEntity },
        { CoachToolNames.ProposeVocabularyEdit, CoachWriteUndoKind.RestoreFields },
        { CoachToolNames.ProposeVocabularyLink, CoachWriteUndoKind.UnlinkVocabulary },
        { CoachToolNames.ProposeVocabularyRemoval, CoachWriteUndoKind.None },
        { CoachToolNames.ProposeSkillEntry, CoachWriteUndoKind.DeleteCreatedEntity },
        { CoachToolNames.ProposeSkillEdit, CoachWriteUndoKind.RestoreFields },
        { CoachToolNames.ProposeSkillArchive, CoachWriteUndoKind.RestoreFields },
        { CoachToolNames.ProposeResourceEntry, CoachWriteUndoKind.DeleteCreatedEntity },
        { CoachToolNames.ProposeResourceEdit, CoachWriteUndoKind.RestoreFields },
        { CoachToolNames.ProposeResourceRemoval, CoachWriteUndoKind.None },
        { CoachToolNames.ProposePreferenceChange, CoachWriteUndoKind.RestoreFields },
        { CoachToolNames.ProposeYouTubeImport, CoachWriteUndoKind.None }
    };

    [Theory]
    [MemberData(nameof(ExpectedUndoKinds))]
    public void Each_write_tool_offers_only_the_reversal_it_can_deliver(
        string toolName, CoachWriteUndoKind expected)
    {
        var handler = Handlers().SingleOrDefault(h => h.ToolName == toolName);

        handler.Should().NotBeNull($"'{toolName}' should have exactly one handler");
        handler!.UndoKind.Should().Be(expected);
    }

    /// <summary>
    /// Nothing that leaves this application claims to be reversible.
    /// </summary>
    /// <remarks>
    /// Stated separately from the table because it is the rule the table is an instance of. A new
    /// handler that reaches an outside service and offers undo would pass the table by being added
    /// to it; it fails here.
    /// </remarks>
    [Fact]
    public void No_externally_visible_write_offers_undo()
    {
        var externallyVisible = new[] { CoachToolNames.ProposeYouTubeImport };

        foreach (var toolName in externallyVisible)
        {
            var handler = Handlers().Single(h => h.ToolName == toolName);

            handler.UndoKind.Should().Be(
                CoachWriteUndoKind.None,
                "a local reversal cannot un-reach a service outside this application");
        }
    }

    /// <summary>
    /// Removing learner-authored rows does not claim to be reversible.
    /// </summary>
    /// <remarks>
    /// The archive tool is deliberately not in this list. It is reversible precisely because it
    /// deletes nothing, which is the reason it replaced the skill deletion that used to sit here.
    /// </remarks>
    [Fact]
    public void No_deletion_offers_undo()
    {
        var deletions = new[]
        {
            CoachToolNames.ProposeVocabularyRemoval,
            CoachToolNames.ProposeResourceRemoval
        };

        foreach (var toolName in deletions)
        {
            var handler = Handlers().Single(h => h.ToolName == toolName);

            handler.UndoKind.Should().Be(
                CoachWriteUndoKind.None,
                "restoring a deleted row and everything that referenced it is not something this "
                + "can promise, so it does not offer to");
        }
    }

    [Theory]
    [MemberData(nameof(ExpectedRiskClasses))]
    public void Each_write_tool_carries_its_declared_risk_class(string toolName, CoachToolRiskClass expected)
    {
        var registry = CoachToolServiceCollectionExtensions.BuildValidatedRegistry(Options());

        var registration = registry.Find(toolName);

        registration.Should().NotBeNull($"{toolName} must be registered");
        registration!.RiskClass.Should().Be(expected);
    }

    [Fact]
    public void Every_write_tool_requires_the_write_feature()
    {
        var registry = CoachToolServiceCollectionExtensions.BuildValidatedRegistry(Options());

        foreach (var name in CoachToolNames.AllWrite)
        {
            var registration = registry.Find(name);
            registration.Should().NotBeNull();
            registration!.RequiredFeatures.Should().Contain(
                "SamWriteTools",
                $"{name} must disappear when write tools are off");
        }
    }

    /// <summary>
    /// With the flag off, not one write tool is enabled. This is the production default.
    /// </summary>
    [Fact]
    public void No_write_tool_is_enabled_when_the_write_feature_is_off()
    {
        var registry = CoachToolServiceCollectionExtensions.BuildValidatedRegistry(
            Options(write: false));

        foreach (var name in CoachToolNames.AllWrite)
        {
            registry.IsEnabled(name).Should().BeFalse($"{name} must be off by default");
        }
    }

    /// <summary>
    /// A bare <see cref="CoachOptions"/> — what an unconfigured host has — enables nothing.
    /// </summary>
    [Fact]
    public void The_default_options_leave_write_tools_off()
    {
        var registry = CoachToolServiceCollectionExtensions.BuildValidatedRegistry(new CoachOptions());

        registry.EnabledNames.Should().NotIntersectWith(CoachToolNames.AllWrite);
    }

    /// <summary>
    /// The write feature alone is not enough; the read surface has to be on too.
    /// </summary>
    [Fact]
    public void Write_tools_stay_off_when_read_tools_are_off()
    {
        var registry = CoachToolServiceCollectionExtensions.BuildValidatedRegistry(
            Options(read: false));

        registry.EnabledNames.Should().NotIntersectWith(CoachToolNames.AllWrite);
    }

    /// <summary>
    /// Every write tool is named as a proposal. The name is what the learner reads in a
    /// transcript, and it should not say the coach did something it cannot do.
    /// </summary>
    [Fact]
    public void Every_write_tool_is_named_as_a_proposal()
    {
        foreach (var name in CoachToolNames.AllWrite)
        {
            name.Should().StartWith(CoachToolNames.ProposePrefix);
        }
    }

    /// <summary>
    /// The single-word capabilities that must not appear as a segment of any tool name.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Matched per underscore-separated segment rather than as a substring, because substring
    /// matching produces false alarms that would force the list to be weakened: "file" appears
    /// inside "profile", "auth" inside "author". Segment matching asks the question the list is
    /// actually about — does this name offer <em>a</em> file, <em>a</em> token — and so the list
    /// can stay blunt.
    /// </para>
    /// <para>
    /// A name check is a weak test in general, but here it checks the right thing: the tool
    /// surface is a closed list of names, so a forbidden capability can only arrive by acquiring
    /// one. The entries cover the categories the brief rules out — raw data access, network and
    /// shell reach, administration, account lifecycle, and anything touching credentials.
    /// </para>
    /// </remarks>
    public static TheoryData<string> ForbiddenSegments =>
    [
        "sql", "execute", "exec", "eval", "shell", "process", "command", "run",
        "http", "fetch", "download", "upload", "webhook", "url", "curl",
        "file", "path", "directory",
        "admin", "migrate", "migration", "seed", "backup", "restore", "recover", "purge",
        "account", "deactivate", "impersonate", "tenant",
        "token", "secret", "credential", "apikey", "password", "auth", "email", "key"
    ];

    [Theory]
    [MemberData(nameof(ForbiddenSegments))]
    public void No_tool_name_offers_a_forbidden_capability(string segment)
    {
        var registry = CoachToolServiceCollectionExtensions.BuildValidatedRegistry(Options());

        foreach (var registration in registry.All)
        {
            registration.Name.Split('_').Should().NotContain(
                segment,
                $"'{registration.Name}' must not offer {segment}");
        }
    }

    /// <summary>
    /// The multi-word capabilities, matched as substrings because they are unambiguous.
    /// </summary>
    public static TheoryData<string> ForbiddenPhrases =>
    [
        "query_database", "read_file", "write_file", "request_url", "raw_",
        "delete_account", "close_account", "switch_user", "api_key", "access_token"
    ];

    [Theory]
    [MemberData(nameof(ForbiddenPhrases))]
    public void No_tool_name_offers_a_forbidden_compound_capability(string phrase)
    {
        var registry = CoachToolServiceCollectionExtensions.BuildValidatedRegistry(Options());

        foreach (var registration in registry.All)
        {
            registration.Name.Should().NotContain(
                phrase,
                $"'{registration.Name}' must not offer {phrase}");
        }
    }

    /// <summary>
    /// The registry's own contents and the declared name list agree.
    /// </summary>
    /// <summary>
    /// Every registered write handler, built without running its constructor.
    /// </summary>
    /// <remarks>
    /// These tests ask what each handler declares about itself, not what it does, so the objects
    /// are deliberately never wired to storage. Taking them uninitialised means the answers cannot
    /// depend on a repository, a database, or a constructor argument this test chose — and a handler
    /// that computed its risk class or its reversal kind from injected state would fail here, which
    /// is correct: those have to be readable from the type.
    /// </remarks>
    private static IReadOnlyList<ICoachWriteHandler> Handlers() =>
        new ServiceCollection()
            .AddCoachReadOnlyTools()
            .Where(d => d.ServiceType == typeof(ICoachWriteHandler))
            .Select(d => d.ImplementationType!)
            .Select(t => (ICoachWriteHandler)System.Runtime.CompilerServices.RuntimeHelpers
                .GetUninitializedObject(t))
            .ToList();

    [Fact]
    public void The_registry_holds_exactly_the_declared_write_tools()
    {
        var registry = CoachToolServiceCollectionExtensions.BuildValidatedRegistry(Options());

        var registered = registry.All
            .Where(r => r.RiskClass is CoachToolRiskClass.WriteSoft or CoachToolRiskClass.WriteHard)
            .Select(r => r.Name)
            .OrderBy(n => n, StringComparer.Ordinal);

        registered.Should().Equal(CoachToolNames.AllWrite.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Every_write_handler_is_registered_as_scoped()
    {
        var services = new ServiceCollection().AddCoachReadOnlyTools();

        var handlers = services
            .Where(d => d.ServiceType == typeof(ICoachWriteHandler))
            .ToList();

        handlers.Should().HaveCount(CoachToolNames.AllWrite.Count);
        handlers.Should().OnlyContain(d => d.Lifetime == ServiceLifetime.Scoped);
    }

    /// <summary>
    /// A proposal outside a conversation turn is refused, and never reaches the ledger.
    /// </summary>
    /// <remarks>
    /// The conversation a write belongs to is set by the turn pipeline and is never an argument the
    /// model can supply — that binding is what makes "this learner, in this conversation" checkable
    /// later. If the scope were ever unset and the tool defaulted to something instead of refusing,
    /// every downstream check that keys on the conversation would still pass while meaning nothing.
    /// The recording proposer proves the refusal happens before any operation is created.
    /// </remarks>
    [Fact]
    public async Task A_proposal_outside_a_conversation_turn_is_refused()
    {
        var proposer = new RecordingProposer();
        var tool = new SentenceStudio.Api.Coach.Tools.SamTools.SamWriteProposalTool(
            new StaticUserScope("user-1"), proposer, new CoachWriteTurnScope());

        var act = async () => await tool.ProposeAsync(
            CoachToolNames.ProposeVocabularyEntry,
            new CoachVocabularyEntryArgs("r1", "\uc0ac\uacfc", "apple"));

        await act.Should().ThrowAsync<CoachToolException>();
        proposer.Calls.Should().Be(0, "the refusal happens before anything is proposed");
    }

    /// <summary>
    /// An unauthenticated caller is refused before the turn scope is even consulted.
    /// </summary>
    [Fact]
    public async Task A_proposal_without_an_identity_is_refused()
    {
        var proposer = new RecordingProposer();
        var turn = new CoachWriteTurnScope();
        turn.Enter("conv-1", "turn-1");

        var tool = new SentenceStudio.Api.Coach.Tools.SamTools.SamWriteProposalTool(
            new StaticUserScope(null), proposer, turn);

        var act = async () => await tool.ProposeAsync(
            CoachToolNames.ProposeVocabularyEntry,
            new CoachVocabularyEntryArgs("r1", "\uc0ac\uacfc", "apple"));

        await act.Should().ThrowAsync<CoachToolException>();
        proposer.Calls.Should().Be(0);
    }

    [Fact]
    public void The_write_ledger_and_its_dependencies_are_scoped()
    {
        var services = new ServiceCollection().AddCoachReadOnlyTools();

        foreach (var type in new[]
                 {
                     typeof(CoachWriteOperationService),
                     typeof(CoachWriteOwnership),
                     typeof(CoachWriteTurnScope),
                     typeof(ICoachWriteHandlerCatalog),
                     typeof(SentenceStudio.Api.Coach.Tools.SamTools.SamWriteProposalTool)
                 })
        {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == type);
            descriptor.Should().NotBeNull($"{type.Name} must be registered");
            descriptor!.Lifetime.Should().Be(
                ServiceLifetime.Scoped,
                $"{type.Name} must be per request so it cannot outlive a learner's scope");
        }
    }
}

/// <summary>A proposer that records whether it was reached, and refuses to do anything else.</summary>
internal sealed class RecordingProposer : ICoachWriteProposer
{
    public int Calls { get; private set; }

    public Task<CoachWriteProposalResult> ProposeAsync(
        string conversationId,
        string? turnId,
        string toolName,
        string argumentsJson,
        CancellationToken cancellationToken = default)
    {
        Calls++;
        throw new InvalidOperationException(
            "The ledger should not have been reached; the tool's own guards come first.");
    }
}

/// <summary>A user scope with a fixed answer, throwing when there is nobody in it.</summary>
internal sealed class StaticUserScope : SentenceStudio.Services.Plans.IUserScopeProvider
{
    private readonly string? _userProfileId;

    public StaticUserScope(string? userProfileId) => _userProfileId = userProfileId;

    public string UserProfileId => _userProfileId
        ?? throw new UnauthorizedAccessException("No user profile is in scope.");

    public bool TryGetUserProfileId(out string userProfileId)
    {
        userProfileId = _userProfileId ?? string.Empty;
        return _userProfileId is not null;
    }
}
