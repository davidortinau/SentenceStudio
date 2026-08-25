using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using FluentAssertions;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.UnitTests.Coach;

/// <summary>
/// Proves the coach contracts cannot carry identity data, answer content, or a write command.
/// </summary>
public class CoachContractPrivacyTests
{
    /// <summary>Names that identify a person. Refused on every coach shape.</summary>
    private static readonly HashSet<string> IdentityWords = new(StringComparer.Ordinal)
    {
        "user", "users", "tenant", "profile", "account", "email", "subject"
    };

    /// <summary>
    /// Names that describe learner content. Refused on the shapes the model can see.
    /// </summary>
    /// <remarks>
    /// Allowed on client-facing contracts, which exist to hand the learner their own material
    /// back. The coach never seeing a due word and the learner reading their own conversation are
    /// two different requirements, and one word list cannot express both.
    /// </remarks>
    private static readonly HashSet<string> ContentWords = new(StringComparer.Ordinal)
    {
        "term", "terms", "gloss", "glosses", "mnemonic", "mnemonics",
        "example", "examples", "sentence", "sentences",
        "diary", "journal", "transcript", "transcripts", "conversation", "conversations"
    };

    /// <summary>Names that would carry an order or an executable. Refused everywhere.</summary>
    private static readonly HashSet<string> DirectiveWords = new(StringComparer.Ordinal)
    {
        "prompt", "prompts", "instruction", "instructions",
        "tool", "tools", "sql", "command", "commands", "script", "scripts"
    };

    /// <summary>Names that would carry a credential. Refused everywhere.</summary>
    private static readonly HashSet<string> SecretWords = new(StringComparer.Ordinal)
    {
        "password", "secret", "secrets", "credential", "credentials", "apikey", "token", "tokens"
    };

    /// <summary>
    /// Names that would expose the storage layer's machinery on a client-facing contract.
    /// </summary>
    private static readonly HashSet<string> InternalStateWords = new(StringComparer.Ordinal)
    {
        "ciphertext", "nonce", "keyid", "digest", "lease", "fencing", "protected", "plaintext"
    };

    /// <summary>Types that would let a caller smuggle free-form or raw tool data.</summary>
    private static readonly Type[] BannedPropertyTypes =
    [
        typeof(object), typeof(JsonElement), typeof(JsonNode), typeof(JsonDocument)
    ];

    /// <summary>
    /// The strict rule, over the shapes the model can see or produce.
    /// </summary>
    /// <remarks>
    /// This is where the embargo earns its keep: the coach plans study time without being shown
    /// the material it is planning about, and a single field named for a due word or a stored
    /// transcript would undo that quietly.
    /// </remarks>
    [Fact]
    public void No_model_facing_shape_names_an_embargoed_field()
    {
        var offenders = Scan(
            CoachContractTypes.IntentShapes,
            [IdentityWords, ContentWords, DirectiveWords, SecretWords]);

        offenders.Should().BeEmpty(
            "shapes the model can see must not carry identity, learner content, orders, or credentials");
    }

    /// <summary>
    /// The bounded rule, over the contracts the server sends to the authenticated owner.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Learner content is deliberately absent from this rule set. A conversation-history contract
    /// returns the learner their own conversation, so refusing the word "conversation" here made
    /// the identifier a REST resource is addressed by look like a leak, and pushed the fix towards
    /// renaming a correct public field or keeping a list of exceptions. Both hide the boundary.
    /// </para>
    /// <para>
    /// What replaces it is stricter where it matters: a public contract must not expose the
    /// storage layer's own machinery either, so ciphertext, nonces, key ids, leases, and
    /// idempotency digests are refused on shapes a client reads.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_client_facing_contract_names_an_embargoed_field()
    {
        var offenders = Scan(
            CoachContractTypes.PublicClientShapes,
            [IdentityWords, DirectiveWords, SecretWords, InternalStateWords]);

        offenders.Should().BeEmpty(
            "client contracts must not carry identity, orders, credentials, or storage machinery");
    }

    private static List<string> Scan(
        IEnumerable<Type> types,
        IReadOnlyList<HashSet<string>> banned)
    {
        var offenders = new List<string>();

        foreach (var type in types)
        {
            foreach (var property in CoachContractTypes.PublicProperties(type))
            {
                foreach (var word in SplitIntoLowercaseWords(property.Name))
                {
                    if (banned.Any(set => set.Contains(word)))
                    {
                        offenders.Add($"{type.Name}.{property.Name} names '{word}'");
                    }
                }
            }
        }

        return offenders;
    }

    /// <summary>Splits a Pascal-case name into lowercase words.</summary>
    private static IEnumerable<string> SplitIntoLowercaseWords(string name)
    {
        var start = 0;
        for (var i = 1; i <= name.Length; i++)
        {
            if (i == name.Length || char.IsUpper(name[i]))
            {
                yield return name[start..i].ToLowerInvariant();
                start = i;
            }
        }
    }

    [Fact]
    public void The_name_scan_finds_an_identity_member()
    {
        var words = SplitIntoLowercaseWords("UserProfileId").ToList();

        words.Should().Contain("user").And.Contain("profile");
        words.Where(IdentityWords.Contains).Should().NotBeEmpty("the scan must find a real violation");
        SplitIntoLowercaseWords("Description").Where(IdentityWords.Contains).Should().BeEmpty();
    }

    /// <summary>
    /// The two scopes really do differ, and they differ in the direction claimed.
    /// </summary>
    /// <remarks>
    /// Without this, the split could silently collapse back into one rule set — either by someone
    /// adding the content words to the public list, or by dropping them from the model list — and
    /// every other test here would still pass. It pins the trade rather than the outcome: content
    /// words move out of the client rules, storage words move in.
    /// </remarks>
    [Fact]
    public void The_two_scopes_trade_content_words_for_storage_words()
    {
        var modelFacing = new[] { IdentityWords, ContentWords, DirectiveWords, SecretWords }
            .SelectMany(set => set).ToHashSet(StringComparer.Ordinal);
        var clientFacing = new[] { IdentityWords, DirectiveWords, SecretWords, InternalStateWords }
            .SelectMany(set => set).ToHashSet(StringComparer.Ordinal);

        modelFacing.Should().Contain("conversation",
            "the model must never be handed a stored transcript");
        clientFacing.Should().NotContain("conversation",
            "a history API cannot address a conversation without naming it");

        clientFacing.Should().Contain("ciphertext").And.Contain("nonce").And.Contain("digest",
            "a client contract must not expose how its history is protected");

        foreach (var word in IdentityWords.Concat(SecretWords).Concat(DirectiveWords))
        {
            modelFacing.Should().Contain(word);
            clientFacing.Should().Contain(word, "identity, credentials, and orders are refused on both sides");
        }
    }

    [Fact]
    public void No_coach_contract_property_uses_an_open_ended_type()
    {
        var offenders = CoachContractTypes.DataShapes
            .SelectMany(t => CoachContractTypes.PublicProperties(t).Select(p => (Type: t, Property: p)))
            .Where(x => BannedPropertyTypes.Contains(Nullable.GetUnderlyingType(x.Property.PropertyType) ?? x.Property.PropertyType))
            .Select(x => $"{x.Type.Name}.{x.Property.Name}")
            .ToList();

        offenders.Should().BeEmpty("an open-ended property type would let raw tool output cross the boundary");
    }

    [Fact]
    public void No_coach_contract_property_uses_a_dictionary()
    {
        var offenders = CoachContractTypes.DataShapes
            .SelectMany(t => CoachContractTypes.PublicProperties(t).Select(p => (Type: t, Property: p)))
            .Where(x => x.Property.PropertyType.IsGenericType
                        && x.Property.PropertyType.GetGenericTypeDefinition().Name.Contains("Dictionary", StringComparison.Ordinal))
            .Select(x => $"{x.Type.Name}.{x.Property.Name}")
            .ToList();

        offenders.Should().BeEmpty("a free-form map would create an untyped escape hatch");
    }

    [Fact]
    public void Every_coach_data_shape_is_sealed()
    {
        var offenders = CoachContractTypes.DataShapes
            .Where(t => !t.IsSealed)
            .Select(t => t.Name)
            .ToList();

        offenders.Should().BeEmpty("a subclass could add a member that the contract tests do not check");
    }

    [Fact]
    public void No_coach_contract_renames_a_member_on_the_wire()
    {
        var offenders = CoachContractTypes.DataShapes
            .SelectMany(t => CoachContractTypes.PublicProperties(t).Select(p => (Type: t, Property: p)))
            .Where(x => x.Property.GetCustomAttribute<JsonPropertyNameAttribute>() is not null)
            .Select(x => $"{x.Type.Name}.{x.Property.Name}")
            .ToList();

        offenders.Should().BeEmpty("the wire name must stay the same as the property name");
    }

    [Fact]
    public void Problem_types_are_unique_and_use_the_repository_prefix()
    {
        var values = typeof(CoachProblemTypes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true, IsInitOnly: false })
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        values.Should().NotBeEmpty();
        values.Should().OnlyHaveUniqueItems();
        values.Should().AllSatisfy(v => v.Should().StartWith("https://sentencestudio.dev/problems/coach-"));
    }
}
