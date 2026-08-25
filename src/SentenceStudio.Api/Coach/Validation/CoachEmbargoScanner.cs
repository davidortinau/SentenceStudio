using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SentenceStudio.Api.Coach.Validation;

/// <summary>
/// Scans a type graph for members the coach must never carry.
/// The scanner refuses identity members, embargoed content members, open-ended
/// member types, domain entity types, and polymorphic types.
/// Run the scanner over every tool answer type at start-up and in the tests.
/// </summary>
/// <remarks>
/// The scanner judges the members <em>declared</em> on the types it walks. Polymorphic types are
/// refused rather than followed for exactly that reason: the serializer can emit a derived shape
/// whose members were never walked, so "the declared graph is clean" would stop implying "the
/// emitted payload is clean". See the <c>polymorphic_type</c> violation.
/// </remarks>
public sealed class CoachEmbargoScanner
{
    /// <summary>
    /// Names that identify a person. Refused everywhere, in every scope.
    /// </summary>
    /// <remarks>
    /// The coach is owner-scoped by construction: the server already knows whose data it is
    /// holding, so no shape it produces and no shape it returns has any reason to say so again.
    /// A payload that names a learner is a payload that can be correlated once it leaves.
    /// </remarks>
    private static readonly HashSet<string> IdentityWords = new(StringComparer.Ordinal)
    {
        "user", "users", "tenant", "profile", "account", "email", "subject"
    };

    /// <summary>
    /// Names that describe learner content the coach must never be shown, and never be able to
    /// smuggle out through a shape it controls.
    /// </summary>
    /// <remarks>
    /// This is the class that separates the scopes. A due word or a stored transcript reaching
    /// the model defeats the whole point of the coach's read-only tool surface. The same words on
    /// a contract the server sends to the authenticated owner are not a leak at all — they are the
    /// feature. A conversation-history API exists precisely to return the learner their own
    /// conversation.
    /// </remarks>
    private static readonly HashSet<string> ContentWords = new(StringComparer.Ordinal)
    {
        "term", "terms", "gloss", "glosses", "mnemonic", "mnemonics",
        "example", "examples", "sentence", "sentences",
        "diary", "journal", "transcript", "transcripts", "conversation", "conversations"
    };

    /// <summary>
    /// Bulk content words refused even on ToolResult envelopes. A tool that returns a single
    /// word's term is fine; a tool that returns all transcripts, memories, or due words is not.
    /// </summary>
    private static readonly HashSet<string> BulkContentWords = new(StringComparer.Ordinal)
    {
        "diary", "journal", "transcript", "transcripts", "conversation", "conversations",
        "mnemonic", "mnemonics"
    };

    /// <summary>
    /// Names that would let a shape carry an order or an executable. Refused in every scope.
    /// </summary>
    /// <remarks>
    /// A field called Prompt or Command is a field someone will eventually put a prompt or a
    /// command in. Refusing the name refuses the mechanism before it exists, which is cheaper than
    /// auditing what flows through it later.
    /// </remarks>
    private static readonly HashSet<string> DirectiveWords = new(StringComparer.Ordinal)
    {
        "prompt", "prompts", "instruction", "instructions",
        "sql", "command", "commands", "script", "scripts"
    };

    /// <summary>Names that would carry a credential. Refused in every scope.</summary>
    private static readonly HashSet<string> SecretWords = new(StringComparer.Ordinal)
    {
        "password", "secret", "secrets", "credential", "credentials", "apikey", "token", "tokens"
    };

    /// <summary>
    /// Names that would expose the storage layer's own machinery through a public contract.
    /// </summary>
    /// <remarks>
    /// Durable coach history is encrypted at rest and coordinated with leases and fencing
    /// versions. None of that is the learner's business, and a client that can read a nonce, a key
    /// id, or an idempotency digest has been handed the shape of the protection scheme for free.
    /// This class is checked on public contracts, where those members would otherwise look like
    /// ordinary metadata.
    /// </remarks>
    private static readonly HashSet<string> InternalStateWords = new(StringComparer.Ordinal)
    {
        "ciphertext", "nonce", "keyid", "digest", "lease", "fencing", "protected", "plaintext"
    };

    /// <summary>Member types that would let free-form or raw data cross the boundary.</summary>
    private static readonly HashSet<Type> BannedMemberTypes =
    [
        typeof(object), typeof(JsonElement), typeof(JsonNode), typeof(JsonDocument)
    ];

    /// <summary>
    /// The only member types a result-scope envelope may carry.
    /// </summary>
    /// <remarks>
    /// An allow-list rather than a ban-list, because the thing being prevented is not a known set
    /// of bad members but an unknown future one. A scope answers "how did you look?" — coverage,
    /// order, filters, counts, dates — and every honest answer to that fits in a flag, a whole
    /// number, a date, or a closed enum. Nothing on this list can carry a term, a gloss, an
    /// example, a transcript fragment, learner prose, or the query string the model supplied,
    /// which is precisely why the list is the rule.
    /// </remarks>
    private static readonly HashSet<Type> ScopeMemberTypes =
    [
        typeof(bool),
        typeof(byte), typeof(short), typeof(int), typeof(long),
        typeof(DateTime), typeof(DateTimeOffset), typeof(DateOnly), typeof(TimeOnly), typeof(TimeSpan)
    ];

    /// <summary>
    /// Namespaces that hold database entities. A coach shape must map to its own
    /// record instead of returning an entity.
    /// </summary>
    private static readonly string[] BannedNamespacePrefixes =
    [
        "SentenceStudio.Shared.Models",
        "SentenceStudio.Data",
        "SentenceStudio.Api.Coach.Persistence"
    ];

    /// <summary>Scans one type and every type it reaches, under the strictest scope.</summary>
    public CoachValidationResult ScanType(Type root) => ScanType(root, CoachEmbargoScope.ModelVisible);

    /// <summary>Scans one type and every type it reaches, under the given scope.</summary>
    public CoachValidationResult ScanType(Type root, CoachEmbargoScope scope)
    {
        ArgumentNullException.ThrowIfNull(root);

        var violations = new List<CoachViolation>();
        var visited = new HashSet<Type>();
        Walk(root, root.Name, scope, violations, visited);
        return CoachValidationResult.From(violations);
    }

    /// <summary>Scans several types in one pass, under the strictest scope.</summary>
    public CoachValidationResult ScanTypes(IEnumerable<Type> roots) =>
        ScanTypes(roots, CoachEmbargoScope.ModelVisible);

    /// <summary>Scans several types in one pass, under the given scope.</summary>
    public CoachValidationResult ScanTypes(IEnumerable<Type> roots, CoachEmbargoScope scope)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var violations = new List<CoachViolation>();
        var visited = new HashSet<Type>();
        foreach (var root in roots)
        {
            Walk(root, root.Name, scope, violations, visited);
        }
        return CoachValidationResult.From(violations);
    }

    /// <summary>The member-name classes refused in the given scope.</summary>
    /// <remarks>
    /// Identity, directives, and secrets are refused everywhere: nothing the coach touches has a
    /// reason to carry them. The three scopes differ on content and storage machinery:
    /// <list type="bullet">
    /// <item><term>ModelVisible</term><description>Refuses all content words.</description></item>
    /// <item><term>ToolResult</term><description>Permits explicit learner-requested content
    /// (term, example, sentence) but still refuses bulk content (transcript, diary, mnemonic).</description></item>
    /// <item><term>PublicClient</term><description>Permits all content; adds internal state word guard.</description></item>
    /// </list>
    /// </remarks>
    private static IEnumerable<HashSet<string>> BannedClassesFor(CoachEmbargoScope scope) =>
        scope switch
        {
            CoachEmbargoScope.ModelVisible =>
                [IdentityWords, ContentWords, DirectiveWords, SecretWords],
            CoachEmbargoScope.ToolResult =>
                [IdentityWords, BulkContentWords, DirectiveWords, SecretWords],
            CoachEmbargoScope.PublicClient =>
                [IdentityWords, DirectiveWords, SecretWords, InternalStateWords],
            // Everything, because a scope carries no payload of any kind and therefore has no
            // reason to name one.
            CoachEmbargoScope.ResultScope =>
                [IdentityWords, ContentWords, DirectiveWords, SecretWords, InternalStateWords],
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope,
                $"Unknown embargo scope '{scope}'. Add a case to the exhaustive switch.")
        };

    private void Walk(
        Type type,
        string path,
        CoachEmbargoScope scope,
        List<CoachViolation> violations,
        HashSet<Type> visited)
    {
        var effective = Unwrap(type);
        if (effective is null || IsScalar(effective) || !visited.Add(effective))
        {
            return;
        }

        // A scope envelope is judged as a scope wherever it is reached from. Inheriting the
        // parent's scope would mean the same shape was strict when it hung off a core-five answer
        // and lax when it hung off a Sam answer, which is the opposite of what a shared metadata
        // envelope should be.
        if (effective.GetCustomAttribute<Tools.CoachScopeShapeAttribute>(inherit: false) is not null)
        {
            scope = CoachEmbargoScope.ResultScope;
        }

        if (IsBannedEntity(effective))
        {
            violations.Add(new CoachViolation(
                CoachViolationKind.Embargo,
                "entity_type",
                $"{path} returns the database entity {effective.Name}. Map it to a coach record."));
            return;
        }

        if (IsPolymorphic(effective, out var polymorphismAttribute))
        {
            // This scanner reasons about the members declared on the type it was handed. A
            // polymorphic type breaks that: the serializer may emit any registered derived type,
            // whose extra members were never walked and were therefore never judged against the
            // embargo. The shape the model or the client actually receives would be a shape
            // nobody reviewed, and adding a derived type later would silently widen the surface
            // with no scan failure to notice it.
            //
            // Refused rather than followed. Enumerating derived types here would make the
            // contract depend on attribute ordering and on assembly load order, and would still
            // miss a derived type registered through a custom type-info resolver. A coach shape
            // is small and closed by design; if a case union is genuinely needed, model it as
            // nullable sibling members on one concrete record so every branch is visible to the
            // scan.
            violations.Add(new CoachViolation(
                CoachViolationKind.Embargo,
                "polymorphic_type",
                $"{path} uses the polymorphic type {effective.Name}, which carries " +
                $"[{polymorphismAttribute}]. Serialization can emit a derived shape whose members " +
                "this contract never examined. Use a single concrete record instead."));
            return;
        }

        foreach (var property in effective.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var memberPath = $"{path}.{property.Name}";
            var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;

            if (!IsCapabilityFlag(property.Name, propertyType))
            {
                var banned = BannedClassesFor(scope);
                foreach (var word in SplitIntoLowercaseWords(property.Name))
                {
                    if (banned.Any(set => set.Contains(word)))
                    {
                        violations.Add(new CoachViolation(
                            CoachViolationKind.Embargo,
                            "member_name",
                            $"{memberPath} names '{word}', which is refused on a {scope} shape."));
                    }
                }
            }

            if (BannedMemberTypes.Contains(propertyType))
            {
                violations.Add(new CoachViolation(
                    CoachViolationKind.Embargo,
                    "member_type",
                    $"{memberPath} uses the open-ended type {propertyType.Name}."));
                continue;
            }

            if (scope == CoachEmbargoScope.ResultScope && !IsAllowedScopeMemberType(propertyType))
            {
                violations.Add(new CoachViolation(
                    CoachViolationKind.Embargo,
                    "scope_member_type",
                    $"{memberPath} uses {propertyType.Name}, which a result scope may not carry. " +
                    "A scope describes how a read answered — coverage, order, filters, counts, " +
                    "dates — so it may only use flags, whole numbers, dates, and closed enums. " +
                    "Anything else, a string above all, is a channel a term, a gloss, an example, " +
                    "a transcript fragment, learner prose, or the model's own query text could " +
                    "travel through."));
                continue;
            }

            if (IsDictionary(propertyType))
            {
                violations.Add(new CoachViolation(
                    CoachViolationKind.Embargo,
                    "member_type",
                    $"{memberPath} uses a map, which is an untyped escape hatch."));
                continue;
            }

            Walk(propertyType, memberPath, scope, violations, visited);
        }
    }

    private static Type? Unwrap(Type type)    {
        var current = Nullable.GetUnderlyingType(type) ?? type;

        if (current.IsArray)
        {
            return current.GetElementType();
        }

        if (current != typeof(string) && current.IsGenericType && typeof(IEnumerable).IsAssignableFrom(current))
        {
            var args = current.GetGenericArguments();
            return args.Length == 1 ? args[0] : current;
        }

        return current;
    }

    private static bool IsScalar(Type type) =>
        type.IsPrimitive
        || type.IsEnum
        || type == typeof(string)
        || type == typeof(decimal)
        || type == typeof(DateTime)
        || type == typeof(DateTimeOffset)
        || type == typeof(DateOnly)
        || type == typeof(TimeOnly)
        || type == typeof(TimeSpan)
        || type == typeof(Guid);

    private static bool IsDictionary(Type type) =>
        type.IsGenericType && type.GetGenericTypeDefinition().Name.Contains("Dictionary", StringComparison.Ordinal);

    /// <summary>
    /// True for the flags, whole numbers, dates, and closed enums a result scope may carry.
    /// </summary>
    /// <remarks>
    /// Deliberately excludes <see cref="string"/>, <see cref="char"/>, floating-point and decimal
    /// numbers, collections, and nested records. Strings and chars are the leak channel the scope
    /// rules exist to close. Collections and nested records are refused because a scope that could
    /// grow a list could grow a list of anything, and the ban is easier to keep than the audit.
    /// Floating-point is refused because a scope reports counts and calendar facts, not measures.
    /// </remarks>
    private static bool IsAllowedScopeMemberType(Type type) =>
        ScopeMemberTypes.Contains(type) || type.IsEnum;

    /// <summary>
    /// True for a yes-or-no capability flag such as HasTranscript.
    /// A boolean carries no content, so it may name an embargoed kind of data.
    /// The flag tells the coach that the server holds the data. It never shows it.
    /// </summary>
    private static bool IsCapabilityFlag(string name, Type type) =>
        type == typeof(bool)
        && (name.StartsWith("Has", StringComparison.Ordinal)
            || name.StartsWith("Is", StringComparison.Ordinal)
            || name.StartsWith("Can", StringComparison.Ordinal)
            || name.StartsWith("Allows", StringComparison.Ordinal)
            || name.StartsWith("Includes", StringComparison.Ordinal));

    private static bool IsBannedEntity(Type type)
    {
        if (type.IsEnum || type.Namespace is null)
        {
            return false;
        }

        return BannedNamespacePrefixes.Any(prefix =>
            type.Namespace.StartsWith(prefix, StringComparison.Ordinal));
    }

    /// <summary>
    /// True when serialization of <paramref name="type"/> can emit a shape other than the one
    /// declared on <paramref name="type"/> itself.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the two attributes that actually turn on System.Text.Json polymorphism count:
    /// <c>[JsonDerivedType]</c> (declared on the base, naming a subtype) and
    /// <c>[JsonPolymorphic]</c> (the options carrier, which enables the feature even with the
    /// derived types supplied by a resolver). Both are looked up without inheriting from base
    /// types, because a derived record in an already-refused hierarchy should be reported at the
    /// base where the decision was made, not once per subtype.
    /// </para>
    /// <para>
    /// Nothing else is refused. <c>[JsonPropertyName]</c>, <c>[JsonIgnore]</c>,
    /// <c>[Description]</c>, <c>[JsonConverter]</c>, and every other annotation leave the emitted
    /// member set equal to the declared member set, which is exactly what this scanner walks, so
    /// they are none of its business.
    /// </para>
    /// </remarks>
    private static bool IsPolymorphic(Type type, out string attributeName)
    {
        if (type.GetCustomAttributes<JsonDerivedTypeAttribute>(inherit: false).Any())
        {
            attributeName = nameof(JsonDerivedTypeAttribute);
            return true;
        }

        if (type.GetCustomAttribute<JsonPolymorphicAttribute>(inherit: false) is not null)
        {
            attributeName = nameof(JsonPolymorphicAttribute);
            return true;
        }

        attributeName = string.Empty;
        return false;
    }

    /// <summary>Splits a Pascal-case name into lowercase words.</summary>
    internal static IEnumerable<string> SplitIntoLowercaseWords(string name)
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
}
