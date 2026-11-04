using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using SentenceStudio.Api.Speech;

namespace SentenceStudio.Api.Tests.Speech;

/// <summary>How the API reads the Aspire-injected media container connection string.</summary>
public sealed class SpeechClipCacheRegistrationTests
{
    private const string AzuriteConnection =
        "DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;" +
        "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;" +
        "BlobEndpoint=http://127.0.0.1:10000/devstoreaccount1;ContainerName=media";

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)))
            .Build();

    private static (ISpeechClipStore? Store, string Description) Create(
        IConfiguration configuration,
        string environment = "Development") =>
        SpeechClipCacheServiceCollectionExtensions.CreateStore(configuration, new FakeEnvironment(environment));

    [Fact]
    public void No_connection_string_leaves_the_cache_off()
    {
        var (store, description) = Create(Config());

        store.Should().BeNull();
        description.Should().Contain("NoConnectionString");
    }

    [Fact]
    public void Azurite_connection_string_uses_the_named_container()
    {
        var (store, description) = Create(Config(("ConnectionStrings:media-container", AzuriteConnection)));

        store.Should().BeOfType<AzureBlobSpeechClipStore>();
        description.Should().Contain("Mode=ConnectionString").And.Contain("Container=media")
            .And.NotContain("AccountKey", "the account key must never reach a log line");
    }

    [Fact]
    public void Azure_endpoint_form_uses_the_managed_identity()
    {
        var (store, description) = Create(Config((
            "ConnectionStrings:media-container",
            "Endpoint=https://storageexample.blob.core.windows.net/;ContainerName=media")));

        store.Should().BeOfType<AzureBlobSpeechClipStore>();
        description.Should().Contain("Mode=ManagedIdentity").And.Contain("Container=media");
    }

    [Fact]
    public void Kill_switch_turns_the_cache_off()
    {
        var (store, description) = Create(Config(
            ("ConnectionStrings:media-container", AzuriteConnection),
            ("SpeechCache:Enabled", "false")));

        store.Should().BeNull();
        description.Should().Contain("TurnedOff");
    }

    [Fact]
    public void Testing_environment_never_builds_a_storage_client()
    {
        var (store, _) = Create(
            Config(("ConnectionStrings:media-container", "Endpoint=https://storageexample.blob.core.windows.net/")),
            environment: "Testing");

        store.Should().BeNull();
    }

    [Fact]
    public void Malformed_connection_string_leaves_the_cache_off_instead_of_failing_startup()
    {
        var (store, description) = Create(Config(("ConnectionStrings:media-container", "not a connection string")));

        store.Should().BeNull();
        description.Should().Contain("InvalidConnectionString").And.NotContain("not a connection string");
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "SentenceStudio.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
