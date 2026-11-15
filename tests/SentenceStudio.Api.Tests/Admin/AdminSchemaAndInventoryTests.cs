using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Admin;
using SentenceStudio.Api.Admin.Persistence;
using SentenceStudio.AppHost;

namespace SentenceStudio.Api.Tests.Admin;

/// <summary>
/// The admin model and the admin migrations describe the same schema. Needs no server; see
/// <c>FeedbackModelMigrationParityTests</c> for why this replaces suppressing the warning.
/// </summary>
public sealed class AdminModelMigrationParityTests
{
    private static AdminDbContext NewContext() =>
        new(new DbContextOptionsBuilder<AdminDbContext>()
            .UseNpgsql(
                "Host=model-parity.invalid;Database=unused",
                npgsql => npgsql.MigrationsHistoryTable(AdminSchema.MigrationsHistoryTable))
            .Options);

    [Fact]
    public void The_admin_model_has_no_changes_the_migrations_do_not_describe()
    {
        using var db = NewContext();

        db.Database.HasPendingModelChanges().Should().BeFalse(
            "a model change without a migration reaches production as a missing column; add the "
            + "migration and update AdminDbContextModelSnapshot");
    }

    [Fact]
    public void The_initial_admin_migration_is_discoverable()
    {
        using var db = NewContext();

        db.Database.GetMigrations().Should().Contain("20261115224514_InitialAdminSchema");
    }

    [Fact]
    public void The_admin_migrations_use_their_own_history_table()
    {
        AdminSchema.MigrationsHistoryTable.Should().Be("__AdminMigrationsHistory");
    }

    /// <summary>
    /// Admin records are not in <c>ApplicationDbContext</c>: they never sync to a device and need
    /// no SQLite twin migration.
    /// </summary>
    [Fact]
    public void The_admin_entities_are_not_registered_on_the_application_context()
    {
        var applicationEntities = typeof(SentenceStudio.Data.ApplicationDbContext)
            .GetProperties()
            .Select(p => p.PropertyType)
            .Where(t => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(DbSet<>))
            .Select(t => t.GenericTypeArguments[0])
            .ToArray();

        applicationEntities.Should().NotContain(typeof(AdminAuditEntry));
    }

    [Fact]
    public void No_admin_context_registration_suppresses_the_pending_model_warning()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src")))
        {
            root = root.Parent;
        }

        root.Should().NotBeNull();

        var offenders = Directory
            .EnumerateFiles(Path.Combine(root!.FullName, "src", "SentenceStudio.Api"), "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(path =>
            {
                var lines = File.ReadAllLines(path);
                for (var i = 0; i < lines.Length; i++)
                {
                    if (!lines[i].Contains("<AdminDbContext>", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // The statement that configures this context's options, to its end.
                    for (var j = i; j < lines.Length && j < i + 12; j++)
                    {
                        var trimmed = lines[j].TrimStart();
                        if (!trimmed.StartsWith("//", StringComparison.Ordinal)
                            && lines[j].Contains("PendingModelChangesWarning", StringComparison.Ordinal))
                        {
                            return true;
                        }

                        if (trimmed.StartsWith("});", StringComparison.Ordinal)
                            || trimmed.StartsWith(".Options", StringComparison.Ordinal))
                        {
                            break;
                        }
                    }
                }

                return false;
            })
            .Select(path => Path.GetRelativePath(root.FullName, path))
            .ToList();

        offenders.Should().BeEmpty(
            "the admin context must not suppress model drift; add the missing migration instead");
    }
}

/// <summary>
/// Every route under <c>/api/admin</c> carries the gate and says what it records, checked against
/// the routes the real host maps.
/// </summary>
public sealed class AdminEndpointInventoryTests : IDisposable
{
    private readonly AdminApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private List<RouteEndpoint> AllEndpoints() =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();

    private static bool IsUnderAdminPrefix(RouteEndpoint endpoint)
    {
        var pattern = "/" + endpoint.RoutePattern.RawText?.TrimStart('/');
        return pattern.Equals(AdminEndpoints.RoutePrefix, StringComparison.OrdinalIgnoreCase)
            || pattern.StartsWith(AdminEndpoints.RoutePrefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_route_under_the_admin_prefix_carries_the_admin_policy_and_the_group_marker()
    {
        var adminRoutes = AllEndpoints().Where(IsUnderAdminPrefix).ToList();

        adminRoutes.Should().NotBeEmpty();
        foreach (var endpoint in adminRoutes)
        {
            endpoint.Metadata.GetMetadata<AdminRouteMetadata>().Should().NotBeNull(
                $"{endpoint.DisplayName} is under the admin prefix and must be mapped on the admin group");
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Should().Contain(a => a.Policy == AdminPolicy.Name,
                    $"{endpoint.DisplayName} must require the admin policy");
            endpoint.Metadata.GetMetadata<IAllowAnonymous>().Should().BeNull(
                $"{endpoint.DisplayName} must not opt out of authorization");
        }
    }

    [Fact]
    public void No_route_outside_the_admin_prefix_carries_the_admin_marker()
    {
        AllEndpoints()
            .Where(e => !IsUnderAdminPrefix(e))
            .Where(e => e.Metadata.GetMetadata<AdminRouteMetadata>() is not null)
            .Select(e => e.DisplayName)
            .Should().BeEmpty();
    }

    [Fact]
    public void Every_admin_route_declares_an_audit_action_or_is_one_of_the_two_known_exceptions()
    {
        var adminRoutes = AllEndpoints().Where(IsUnderAdminPrefix).ToList();

        var unaudited = new List<string>();
        var actions = new List<string>();
        foreach (var endpoint in adminRoutes)
        {
            var action = endpoint.Metadata.GetMetadata<AdminActionMetadata>();
            var exception = endpoint.Metadata.GetMetadata<AdminUnauditedMetadata>();
            (action is null).Should().NotBe(exception is null,
                $"{endpoint.DisplayName} must declare exactly one of an audit action or a no-audit reason");

            if (action is not null)
            {
                actions.Add(action.Action);
            }
            else
            {
                unaudited.Add(endpoint.RoutePattern.RawText!);
            }
        }

        // Adding a route that leaves no audit row means changing this list on purpose.
        unaudited.Should().BeEquivalentTo("/api/admin/access", "/api/admin/{**rest}");

        var known = typeof(AdminActions).GetFields()
            .Where(f => f.IsLiteral)
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();
        actions.Should().OnlyContain(a => known.Contains(a));
        actions.Should().OnlyHaveUniqueItems("one action per route keeps the audit log unambiguous");
        actions.Should().OnlyContain(a => a.Length <= AdminAuditLimits.MaxCodeLength);
    }

    [Fact]
    public void No_phase_0_route_asks_for_the_password_again_yet()
    {
        // Pins the list of dangerous routes: each one added later is added here on purpose.
        AllEndpoints()
            .Where(e => e.Metadata.GetMetadata<AdminStepUpMetadata>() is not null)
            .Select(e => e.RoutePattern.RawText)
            .Should().BeEmpty();
    }
}

/// <summary>The AppHost forwards <c>Admin:Emails</c> to the API, and nothing when none is set.</summary>
public sealed class AdminAppHostConfigurationTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e => new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void Nothing_configured_forwards_nothing_so_the_API_has_no_admins()
    {
        AdminConfigurationReader.ReadApiEnvironment(Config()).Should().BeEmpty();
    }

    [Fact]
    public void Configured_addresses_are_forwarded_trimmed_compacted_and_without_repeats()
    {
        var forwarded = AdminConfigurationReader.ReadApiEnvironment(Config(
            ("Admin:Emails:0", " captain@example.test "),
            ("Admin:Emails:1", "  "),
            ("Admin:Emails:3", "CAPTAIN@example.test"),
            ("Admin:Emails:5", "mate@example.test")));

        forwarded.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["Admin__Emails__0"] = "captain@example.test",
            ["Admin__Emails__1"] = "mate@example.test"
        });
    }

    [Fact]
    public void The_AppHost_forwards_the_list_from_the_mode_appropriate_source()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !Directory.Exists(Path.Combine(root.FullName, "src")))
        {
            root = root.Parent;
        }

        var appHost = File.ReadAllText(Path.Combine(root!.FullName, "src", "SentenceStudio.AppHost", "AppHost.cs"));

        // coachConfiguration is the environment-only view when publishing, so user secrets and
        // appsettings.Development.json can never put an admin address into a deployment.
        appHost.Should().Contain("AdminConfigurationReader.ReadApiEnvironment(coachConfiguration)");
    }
}
