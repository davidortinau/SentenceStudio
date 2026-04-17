using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Maui.Storage;
using Plugin.Maui.HelpKit.Storage.Migrations;
using SQLite;

namespace Plugin.Maui.HelpKit.Storage;

/// <summary>
/// Owns the <see cref="SQLiteConnection"/> for the HelpKit database and
/// exposes a serialized lock object. Repositories acquire <see cref="SyncRoot"/>
/// around every write to keep sqlite-net-pcl happy under concurrent use.
/// </summary>
/// <remarks>
/// Lifetime: registered as a singleton. The connection stays open for the
/// life of the process; <see cref="Dispose"/> is called by DI on shutdown.
/// </remarks>
internal sealed class HelpKitDatabase : IDisposable
{
    private readonly ILogger<HelpKitDatabase> _logger;
    private readonly HelpKitOptions _options;
    private SQLiteConnection? _connection;
    private bool _initialized;

    public HelpKitDatabase(IOptions<HelpKitOptions> options, ILogger<HelpKitDatabase> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Synchronization root for write operations.</summary>
    public object SyncRoot { get; } = new();

    /// <summary>Absolute storage directory. Created lazily on first access.</summary>
    public string StorageDirectory => ResolveStorageDirectory(_options);

    /// <summary>Absolute path to the SQLite file.</summary>
    public string DatabasePath => Path.Combine(StorageDirectory, "helpkit.db");

    /// <summary>Absolute path used for vector JSON persistence.</summary>
    public string VectorsJsonPath => Path.Combine(StorageDirectory, "vectors.json");

    /// <summary>
    /// Returns the open connection, initializing the database and running
    /// migrations on first access. Thread-safe.
    /// </summary>
    public SQLiteConnection Connection
    {
        get
        {
            EnsureInitialized();
            return _connection!;
        }
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        lock (SyncRoot)
        {
            if (_initialized) return;

            var dir = StorageDirectory;
            Directory.CreateDirectory(dir);

            var flags = SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create | SQLiteOpenFlags.FullMutex | SQLiteOpenFlags.SharedCache;
            _connection = new SQLiteConnection(DatabasePath, flags, storeDateTimeAsTicks: true);

            _logger.LogInformation("HelpKit database opened at {Path}", DatabasePath);

            MigrationRunner.Apply(_connection, _logger);
            _initialized = true;
        }
    }

    /// <summary>
    /// Resolves the storage directory from options, falling back to
    /// <c>{FileSystem.AppDataDirectory}/helpkit</c>. Exposed internally so
    /// tests can supply an explicit path without a running MAUI host.
    /// </summary>
    internal static string ResolveStorageDirectory(HelpKitOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.StoragePath))
            return options.StoragePath!;

        return Path.Combine(FileSystem.AppDataDirectory, "helpkit");
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}
