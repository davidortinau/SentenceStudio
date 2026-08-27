using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using SentenceStudio.Api.Coach.Tools;
using SentenceStudio.Data;

namespace SentenceStudio.Api.Tests.Coach.Tools;

/// <summary>
/// The persistence boundary for the coach tool surface.
/// </summary>
/// <remarks>
/// <para>
/// A coach tool is a model-facing read: it decides what a language model is allowed to see about
/// a learner. When a tool writes its own LINQ against <c>ApplicationDbContext</c>, the predicate
/// that decides "this learner owns this row" lives in the tool, and the same predicate lives again
/// in whichever repository the app screen uses. Two copies of an ownership rule is one copy too
/// many: they drift, and the drift is silent, because the screen keeps working while the model
/// starts answering from a slightly different set of rows than the learner can see.
/// </para>
/// <para>
/// So the rule is structural rather than advisory. A tool asks an application service for facts.
/// The service owns the table, the tenant predicate, the ordering, and the projection, and the app
/// surface asks the same service the same way. This test is what keeps the rule true after the
/// people who agreed to it have moved on.
/// </para>
/// <para>
/// Two checks, because either alone has a hole. The source scan catches a context used only inside
/// a method body, which never appears in any signature reflection can see. Reflection catches a
/// type that reaches the context through a using alias, a generic argument, or a partial file the
/// scan's directory filter missed. A violation has to defeat both.
/// </para>
/// </remarks>
public class CoachToolBoundaryArchitectureTests
{
    private const string ForbiddenType = "ApplicationDbContext";

    private static readonly string ToolsNamespace = "SentenceStudio.Api.Coach.Tools";

    [Fact]
    public void CoachToolSources_DoNotReferenceApplicationDbContext()
    {
        var toolsDirectory = Path.Combine(
            RepositoryRoot(), "src", "SentenceStudio.Api", "Coach", "Tools");

        Directory.Exists(toolsDirectory).Should().BeTrue(
            "the coach tool surface must exist for this boundary to mean anything");

        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(toolsDirectory, "*.cs", SearchOption.AllDirectories))
        {
            var code = StripComments(File.ReadAllText(file));
            var matches = Regex.Matches(code, $@"\b{ForbiddenType}\b");
            if (matches.Count == 0)
            {
                continue;
            }

            var relative = Path.GetRelativePath(RepositoryRoot(), file).Replace('\\', '/');
            offenders.Add($"{relative} ({matches.Count} reference(s))");
        }

        offenders.Should().BeEmpty(
            "a coach tool must read through an application service that owns the tenant predicate, "
            + "the ordering, and the projection — not through its own LINQ over the DbContext. "
            + "Offending files:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void CoachToolTypes_DoNotDeclareApplicationDbContextMembers()
    {
        var offenders = new List<string>();

        foreach (var type in typeof(CoachToolBase).Assembly.GetTypes())
        {
            if (type.Namespace is null || !IsToolNamespace(type.Namespace))
            {
                continue;
            }

            const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (var ctor in type.GetConstructors(All))
            {
                foreach (var parameter in ctor.GetParameters())
                {
                    if (MentionsForbiddenType(parameter.ParameterType))
                    {
                        offenders.Add($"{type.FullName}..ctor({parameter.Name})");
                    }
                }
            }

            foreach (var field in type.GetFields(All))
            {
                if (MentionsForbiddenType(field.FieldType))
                {
                    offenders.Add($"{type.FullName}.{field.Name} (field)");
                }
            }

            foreach (var property in type.GetProperties(All))
            {
                if (MentionsForbiddenType(property.PropertyType))
                {
                    offenders.Add($"{type.FullName}.{property.Name} (property)");
                }
            }

            foreach (var method in type.GetMethods(All))
            {
                if (MentionsForbiddenType(method.ReturnType))
                {
                    offenders.Add($"{type.FullName}.{method.Name} (return type)");
                }

                foreach (var parameter in method.GetParameters())
                {
                    if (MentionsForbiddenType(parameter.ParameterType))
                    {
                        offenders.Add($"{type.FullName}.{method.Name}({parameter.Name})");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            "no type on the coach tool surface may take, hold, or hand back an ApplicationDbContext. "
            + "Offending members:\n" + string.Join("\n", offenders.Distinct()));
    }

    private static bool IsToolNamespace(string ns) =>
        ns == ToolsNamespace || ns.StartsWith(ToolsNamespace + ".", StringComparison.Ordinal);

    /// <summary>
    /// True when the type is the forbidden context, or carries it as an array element, a by-ref
    /// target, or a generic argument at any depth — so <c>Task&lt;ApplicationDbContext&gt;</c> and
    /// <c>Func&lt;ApplicationDbContext&gt;</c> are caught alongside the bare type.
    /// </summary>
    private static bool MentionsForbiddenType(Type type)
    {
        if (type == typeof(ApplicationDbContext))
        {
            return true;
        }

        if (type.HasElementType && type.GetElementType() is { } element)
        {
            return MentionsForbiddenType(element);
        }

        return type.IsGenericType && type.GetGenericArguments().Any(MentionsForbiddenType);
    }

    /// <summary>
    /// Removes line comments, block comments, string literals, and char literals so the scan reads
    /// code only. Prose that explains why the boundary exists must not itself trip the boundary.
    /// </summary>
    private static string StripComments(string source)
    {
        var output = new StringBuilder(source.Length);
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i = Math.Min(i + 2, source.Length);
                continue;
            }

            if (c == '"' && output.Length > 0 && output[^1] == '@')
            {
                // Verbatim string: no backslash escapes, and "" is an escaped quote.
                i++;
                while (i < source.Length)
                {
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"') { i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            if (c == '"' || c == '\'')
            {
                var quote = c;
                i++;
                while (i < source.Length && source[i] != quote && source[i] != '\n')
                {
                    if (source[i] == '\\') i++;
                    i++;
                }
                i++;
                continue;
            }

            output.Append(c);
            i++;
        }

        return output.ToString();
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("Could not locate the repository root.");
    }
}
