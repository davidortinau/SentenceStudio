using Microsoft.AspNetCore.Mvc.Testing;
using SentenceStudio.WebApp.Tests.Infrastructure;

namespace SentenceStudio.WebApp.Tests.Storage;

public sealed class IsolatedAppDataRootTests
{
    [Fact]
    public void Operator_test_hosts_use_unique_owned_roots_inside_the_test_output()
    {
        using var first = new OperatorWebAppFactory("scratch-first");
        using var second = new OperatorWebAppFactory("scratch-second");

        var relative = Path.GetRelativePath(AppContext.BaseDirectory, first.AppDataRoot);
        Assert.StartsWith($"webapp-test-data{Path.DirectorySeparatorChar}", relative, StringComparison.Ordinal);
        Assert.NotEqual(first.AppDataRoot, second.AppDataRoot);
    }

    [Fact]
    public void Relative_isolation_root_is_rejected_before_the_host_starts()
    {
        const string key = "WebApp__AppDataRoot";
        var previous = Environment.GetEnvironmentVariable(key);

        try
        {
            Environment.SetEnvironmentVariable(key, "relative-test-storage");
            using var factory = new WebApplicationFactory<Program>();

            var failure = Record.Exception(() => _ = factory.Services);

            Assert.NotNull(failure);
            Assert.Contains(
                "WebApp:AppDataRoot must be a non-empty absolute path",
                failure.ToString(),
                StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }
}
