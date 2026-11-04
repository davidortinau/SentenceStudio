using System.Text;
using Azure.Storage.Blobs;
using Microsoft.Extensions.Configuration;
using SentenceStudio.Api.Speech;

namespace SentenceStudio.Api.Tests.Speech;

/// <summary>
/// The real blob store against a disposable Azurite. Skipped unless
/// <c>SENTENCESTUDIO_TEST_AZURITE_BLOB_CONNECTION</c> names one, for example:
/// <code>
/// docker run -d --rm --name ss-speechcache-azurite -p 127.0.0.1:0:10000 \
///   mcr.microsoft.com/azure-storage/azurite:3.35.0 azurite-blob --blobHost 0.0.0.0 --skipApiVersionCheck
/// export SENTENCESTUDIO_TEST_AZURITE_BLOB_CONNECTION="DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;BlobEndpoint=http://127.0.0.1:&lt;port&gt;/devstoreaccount1"
/// </code>
/// Never point it at a shared or production account: each test creates and deletes its own
/// <c>speechcache-test-*</c> container.
/// </summary>
public sealed class AzureBlobSpeechClipStoreAzuriteTests : IAsyncLifetime
{
    private BlobContainerClient _container = null!;

    public async Task InitializeAsync()
    {
        if (AzuriteServer.SkipReason is not null)
        {
            return;
        }

        _container = new BlobServiceClient(AzuriteServer.ConnectionString)
            .GetBlobContainerClient($"speechcache-test-{Guid.NewGuid():N}");
        await _container.CreateAsync();
    }

    public async Task DisposeAsync()
    {
        if (AzuriteServer.SkipReason is null && _container is not null)
        {
            await _container.DeleteIfExistsAsync();
        }
    }

    [AzuriteFact]
    public async Task Missing_blob_reads_as_null()
    {
        var store = new AzureBlobSpeechClipStore(_container);

        (await store.ReadAsync("speech-cache/v1/synthesize/absent.mp3", CancellationToken.None)).Should().BeNull();
    }

    [AzuriteFact]
    public async Task Written_clip_round_trips_with_content_type_and_no_metadata()
    {
        var store = new AzureBlobSpeechClipStore(_container);
        var name = SpeechClipCacheKey.BlobName(SpeechClipSpec.ForSynthesize("v", "eleven_v3", "안녕하세요"));
        var bytes = Encoding.UTF8.GetBytes("mp3-bytes");

        (await store.WriteAsync(name, bytes, SpeechClipCache.AudioContentType, overwrite: false, CancellationToken.None))
            .Should().BeTrue();

        (await store.ReadAsync(name, CancellationToken.None)).Should().Equal(bytes);
        var properties = (await _container.GetBlobClient(name).GetPropertiesAsync()).Value;
        properties.ContentType.Should().Be("audio/mpeg");
        properties.Metadata.Should().BeEmpty("no text, user or resource may be stored with the clip");
    }

    [AzuriteFact]
    public async Task Write_if_absent_keeps_the_first_stored_clip()
    {
        var store = new AzureBlobSpeechClipStore(_container);
        const string name = "speech-cache/v1/synthesize/first-wins.mp3";

        await store.WriteAsync(name, [1, 1, 1], SpeechClipCache.AudioContentType, overwrite: false, CancellationToken.None);
        var second = await store.WriteAsync(name, [2, 2, 2], SpeechClipCache.AudioContentType, overwrite: false, CancellationToken.None);

        second.Should().BeFalse();
        (await store.ReadAsync(name, CancellationToken.None)).Should().Equal(1, 1, 1);
    }

    [AzuriteFact]
    public async Task Overwrite_replaces_an_unreadable_entry()
    {
        var store = new AzureBlobSpeechClipStore(_container);
        const string name = "speech-cache/v1/synthesize-timestamped/bad.json";

        await store.WriteAsync(name, [0], SpeechClipCache.TimestampedContentType, overwrite: false, CancellationToken.None);
        var replaced = await store.WriteAsync(name, [7, 7], SpeechClipCache.TimestampedContentType, overwrite: true, CancellationToken.None);

        replaced.Should().BeTrue();
        (await store.ReadAsync(name, CancellationToken.None)).Should().Equal(7, 7);
    }

    [AzuriteFact]
    public async Task Missing_container_is_a_fault_and_the_cache_falls_back_to_the_provider()
    {
        var missing = new BlobServiceClient(AzuriteServer.ConnectionString)
            .GetBlobContainerClient($"speechcache-missing-{Guid.NewGuid():N}");
        var store = new AzureBlobSpeechClipStore(missing);

        var read = () => store.ReadAsync("speech-cache/v1/synthesize/x.mp3", CancellationToken.None);
        await read.Should().ThrowAsync<Azure.RequestFailedException>();

        var provider = new FakeSpeechClipProvider();
        var logger = new CapturingLogger<SpeechClipCache>();
        var audio = await new SpeechClipCache(logger, store)
            .GetOrGenerateAsync(SpeechClipSpec.ForSynthesize("v", "eleven_v3", "text"), provider, CancellationToken.None);

        audio.Should().NotBeEmpty();
        provider.Calls.Should().Be(1);
        logger.Entries.Should().Contain(e => e.Message.Contains("ContainerNotFound"));
    }

    [AzuriteFact]
    public async Task Store_built_by_the_api_registration_reads_and_writes_the_named_container()
    {
        // The API's own construction: separate read and write clients (writes never retry).
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:media-container"] =
                    $"{AzuriteServer.ConnectionString};ContainerName={_container.Name}"
            })
            .Build();
        var (store, _) = SpeechClipCacheServiceCollectionExtensions.CreateStore(
            configuration, new DevelopmentEnvironment());
        const string name = "speech-cache/v1/synthesize/registration.mp3";

        (await store!.WriteAsync(name, [4, 5, 6], SpeechClipCache.AudioContentType, overwrite: false, CancellationToken.None))
            .Should().BeTrue();

        (await store.ReadAsync(name, CancellationToken.None)).Should().Equal(4, 5, 6);
        (await _container.GetBlobClient(name).ExistsAsync()).Value.Should().BeTrue();
    }

    private sealed class DevelopmentEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "SentenceStudio.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    [AzuriteFact]
    public async Task Cache_over_real_blob_storage_pays_once_then_serves_the_stored_clip()
    {
        var provider = new FakeSpeechClipProvider();
        var cache = new SpeechClipCache(new CapturingLogger<SpeechClipCache>(), new AzureBlobSpeechClipStore(_container));
        var plain = SpeechClipSpec.ForSynthesize("v", "eleven_v3", "같은 문장");
        var timed = SpeechClipSpec.ForTimestamped("v", 0.5f, 0.75f, 1.0f, "같은 대본");

        var first = await cache.GetOrGenerateAsync(plain, provider, CancellationToken.None);
        var again = await cache.GetOrGenerateAsync(plain, provider, CancellationToken.None);
        var timedFirst = await cache.GetOrGenerateTimestampedAsync(timed, provider, CancellationToken.None);
        var timedAgain = await cache.GetOrGenerateTimestampedAsync(timed, provider, CancellationToken.None);

        provider.Calls.Should().Be(2);
        again.Should().Equal(first);
        timedAgain.Audio.Should().Equal(timedFirst.Audio);
        timedAgain.Characters.Should().BeEquivalentTo(timedFirst.Characters, o => o.WithStrictOrdering());
        timedAgain.DurationSeconds.Should().Be(timedFirst.DurationSeconds);
    }
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class AzuriteFactAttribute : FactAttribute
{
    public AzuriteFactAttribute()
    {
        Skip = AzuriteServer.SkipReason;
    }
}

internal static class AzuriteServer
{
    public const string ConnectionVariable = "SENTENCESTUDIO_TEST_AZURITE_BLOB_CONNECTION";

    private static readonly Lazy<string?> Probe = new(ProbeServer);

    public static string? ConnectionString => Environment.GetEnvironmentVariable(ConnectionVariable);

    public static string? SkipReason => Probe.Value;

    private static string? ProbeServer()
    {
        var connection = ConnectionString;
        if (string.IsNullOrWhiteSpace(connection))
        {
            return $"No disposable Azurite configured. Set {ConnectionVariable} to run the real blob store tests.";
        }

        // Refuse anything that looks like a real account; these tests create and delete containers.
        if (!connection.Contains("devstoreaccount1", StringComparison.Ordinal)
            || !connection.Contains("127.0.0.1", StringComparison.Ordinal))
        {
            return $"{ConnectionVariable} must name a local Azurite (devstoreaccount1 on 127.0.0.1).";
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            new BlobServiceClient(connection).GetProperties(cts.Token);
            return null;
        }
        catch (Exception ex)
        {
            return $"Azurite unavailable: {ex.GetType().Name}";
        }
    }
}
