#if DEBUG
using System.Data;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;

namespace SentenceStudio.MacOS;

public sealed class MigrationValidationOptionsTests : IDisposable
{
    private static readonly Encoding MarkerEncoding =
        new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    private readonly string _testRoot;

    public MigrationValidationOptionsTests()
    {
        var created = Directory.CreateTempSubdirectory("ss-migration-options-").FullName;
        _testRoot = MigrationValidationOptions.ResolvePhysicalPath(created);
        SetPrivateDirectoryMode(_testRoot);
    }

    [Fact]
    public void No_validation_arguments_leave_normal_startup_unchanged()
    {
        var liveDatabase = CreateLiveDatabasePath();

        var options = MigrationValidationOptions.FromCommandLine(
            new[] { "SentenceStudio", "-psn_0_12345" },
            liveDatabase);

        Assert.Null(options);
    }

    [Fact]
    public void Explicit_mode_can_be_detected_before_platform_services_are_available()
    {
        Assert.True(MigrationValidationOptions.IsRequested(
            new[] { "SentenceStudio", MigrationValidationOptions.ModeArgument }));
        Assert.False(MigrationValidationOptions.IsRequested(
            new[] { "SentenceStudio", MigrationValidationOptions.DatabaseArgument, "/tmp/test.db3" }));
    }

    [Fact]
    public void Complete_one_shot_arguments_accept_an_isolated_database_once()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var liveDatabase = CreateLiveDatabasePath();
        var paths = CreateValidationPaths();
        var arguments = ValidArguments(paths);

        var options = MigrationValidationOptions.FromCommandLine(arguments, liveDatabase);
        Assert.NotNull(options);
        try
        {
            Assert.Equal(paths.Root, options.RootPath);
            Assert.Equal(paths.Database, options.DatabasePath);
            Assert.Equal(19225, options.DevFlowPort);
            Assert.Equal(paths.LaunchToken, options.LaunchToken);
            Assert.Equal(ConnectionState.Open, options.Connection.State);
            Assert.Equal(paths.Identity.Device, options.OpenedDatabaseIdentity.Device);
            Assert.Equal(paths.Identity.Inode, options.OpenedDatabaseIdentity.Inode);
            Assert.True(options.OpenedBinding.FileDescriptor > 2);
            Assert.False(File.Exists(Path.Combine(paths.Root, MigrationValidationOptions.MarkerFileName)));

            using var command = options.Connection.CreateCommand();
            command.CommandText =
                "SELECT file FROM pragma_database_list WHERE name = 'main';";
            var openedMain = Assert.IsType<string>(command.ExecuteScalar());
            Assert.Equal(
                paths.Database,
                MigrationValidationOptions.ResolvePhysicalPath(openedMain));
            command.CommandText = "PRAGMA foreign_keys;";
            Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));

            var replayError = Assert.Throws<InvalidOperationException>(
                () => MigrationValidationOptions.FromCommandLine(arguments, liveDatabase));
            Assert.Contains("does not exist", replayError.Message);
        }
        finally
        {
            options.Dispose();
        }

        Assert.Equal(ConnectionState.Closed, options.Connection.State);
    }

    [Theory]
    [InlineData(MigrationValidationOptions.RootArgument)]
    [InlineData(MigrationValidationOptions.DatabaseArgument)]
    [InlineData(MigrationValidationOptions.DatabaseDeviceArgument)]
    [InlineData(MigrationValidationOptions.DatabaseInodeArgument)]
    [InlineData(MigrationValidationOptions.TokenArgument)]
    [InlineData(MigrationValidationOptions.LaunchTokenArgument)]
    [InlineData(MigrationValidationOptions.DevFlowPortArgument)]
    [InlineData(MigrationValidationOptions.DisableSyncArgument)]
    public void Missing_required_validation_argument_fails_closed(string argumentToRemove)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var liveDatabase = CreateLiveDatabasePath();
        var arguments = ValidArguments(CreateValidationPaths()).ToList();
        var index = arguments.IndexOf(argumentToRemove);
        arguments.RemoveAt(index);
        if (argumentToRemove != MigrationValidationOptions.DisableSyncArgument)
        {
            arguments.RemoveAt(index);
        }

        var error = Assert.Throws<InvalidOperationException>(
            () => MigrationValidationOptions.FromCommandLine(arguments, liveDatabase));

        Assert.Contains("requires exactly one", error.Message);
    }

    [Fact]
    public void Validation_database_outside_the_private_root_is_rejected()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var liveDatabase = CreateLiveDatabasePath();
        var paths = CreateValidationPaths();
        var outsideDirectory = CreatePrivateDirectory(Path.Combine(_testRoot, "outside"));
        var outsideDatabase = CreatePrivateFile(Path.Combine(outsideDirectory, "sstudio.db3"));
        var outsideIdentity = MigrationValidationOptions.GetFileIdentity(outsideDatabase);
        var arguments = ValidArguments(paths);
        ReplaceArgumentValue(
            arguments,
            MigrationValidationOptions.DatabaseArgument,
            outsideDatabase);
        ReplaceArgumentValue(
            arguments,
            MigrationValidationOptions.DatabaseDeviceArgument,
            outsideIdentity.Device.ToString());
        ReplaceArgumentValue(
            arguments,
            MigrationValidationOptions.DatabaseInodeArgument,
            outsideIdentity.Inode.ToString());

        var error = Assert.Throws<InvalidOperationException>(
            () => MigrationValidationOptions.FromCommandLine(arguments, liveDatabase));

        Assert.Contains("inside the validation root", error.Message);
    }

    [Fact]
    public void Validation_root_inside_live_app_data_is_rejected()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var liveDirectory = CreatePrivateDirectory(Path.Combine(_testRoot, "live"));
        var liveDatabase = Path.Combine(liveDirectory, "sstudio.db3");
        var paths = CreateValidationPaths(Path.Combine(liveDirectory, "validator"));

        var error = Assert.Throws<InvalidOperationException>(
            () => MigrationValidationOptions.FromCommandLine(
                ValidArguments(paths),
                liveDatabase));

        Assert.Contains("live application data directory", error.Message);
    }

    [Fact]
    public void Non_temp_validation_root_is_rejected()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var nonTempRoot = Path.Combine(
            AppContext.BaseDirectory,
            $"non-temp-validator-{Guid.NewGuid():N}");
        try
        {
            var paths = CreateValidationPaths(nonTempRoot);

            var error = Assert.Throws<InvalidOperationException>(
                () => MigrationValidationOptions.FromCommandLine(
                    ValidArguments(paths),
                    CreateLiveDatabasePath()));

            Assert.Contains("physical temporary directory", error.Message);
        }
        finally
        {
            if (Directory.Exists(nonTempRoot))
            {
                Directory.Delete(nonTempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void Relative_paths_are_rejected()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var liveDatabase = CreateLiveDatabasePath();
        var paths = CreateValidationPaths();
        var arguments = ValidArguments(paths);
        ReplaceArgumentValue(
            arguments,
            MigrationValidationOptions.RootArgument,
            "relative-root");

        var error = Assert.Throws<InvalidOperationException>(
            () => MigrationValidationOptions.FromCommandLine(arguments, liveDatabase));

        Assert.Contains("must be absolute", error.Message);
    }

    [Fact]
    public void Mismatched_marker_token_is_consumed_and_rejected()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var paths = CreateValidationPaths();
        var arguments = ValidArguments(paths);
        ReplaceArgumentValue(arguments, MigrationValidationOptions.TokenArgument, "wrong-token");

        var error = Assert.Throws<InvalidOperationException>(
            () => MigrationValidationOptions.FromCommandLine(
                arguments,
                CreateLiveDatabasePath()));

        Assert.Contains("token does not match", error.Message);
        Assert.False(File.Exists(Path.Combine(paths.Root, MigrationValidationOptions.MarkerFileName)));
    }

    [Fact]
    public void Symbolic_link_ancestor_is_rejected_before_physical_resolution()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var targetParent = CreatePrivateDirectory(Path.Combine(_testRoot, "physical-parent"));
        var paths = CreateValidationPaths(Path.Combine(targetParent, "validator"));
        var aliasParent = Path.Combine(_testRoot, "alias-parent");
        Directory.CreateSymbolicLink(aliasParent, targetParent);
        var aliasRoot = Path.Combine(aliasParent, "validator");
        var aliasDatabase = Path.Combine(aliasRoot, "database", "sstudio.db3");
        var arguments = ValidArguments(paths);
        ReplaceArgumentValue(arguments, MigrationValidationOptions.RootArgument, aliasRoot);
        ReplaceArgumentValue(arguments, MigrationValidationOptions.DatabaseArgument, aliasDatabase);

        var error = Assert.Throws<InvalidOperationException>(
            () => MigrationValidationOptions.FromCommandLine(
                arguments,
                CreateLiveDatabasePath()));

        Assert.Contains("symbolic links", error.Message);
    }

    [Fact]
    public void Symbolic_link_database_is_rejected()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var paths = CreateValidationPaths();
        File.Delete(paths.Database);
        var linkTarget = CreatePrivateFile(Path.Combine(_testRoot, "link-target.db3"));
        File.CreateSymbolicLink(paths.Database, linkTarget);
        var arguments = ValidArguments(paths);
        var linkIdentity = MigrationValidationOptions.GetFileIdentity(paths.Database);
        ReplaceArgumentValue(
            arguments,
            MigrationValidationOptions.DatabaseDeviceArgument,
            linkIdentity.Device.ToString());
        ReplaceArgumentValue(
            arguments,
            MigrationValidationOptions.DatabaseInodeArgument,
            linkIdentity.Inode.ToString());

        var error = Assert.Throws<InvalidOperationException>(
            () => MigrationValidationOptions.FromCommandLine(
                arguments,
                CreateLiveDatabasePath()));

        Assert.Contains("symbolic links", error.Message);
    }

    [Fact]
    public void Hardlinked_database_is_rejected_by_link_count()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var paths = CreateValidationPaths();
        CreateHardLink(Path.Combine(paths.Root, "database", "alias.db3"), paths.Database);

        var error = Assert.Throws<InvalidOperationException>(
            () => MigrationValidationOptions.FromCommandLine(
                ValidArguments(paths),
                CreateLiveDatabasePath()));

        Assert.Contains("link count one", error.Message);
    }

    [Fact]
    public void Database_with_live_device_and_inode_is_rejected()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var paths = CreateValidationPaths();
        var liveDirectory = CreatePrivateDirectory(
            Path.Combine(_testRoot, $"live-alias-{Guid.NewGuid():N}"));
        var liveDatabase = Path.Combine(liveDirectory, "sstudio.db3");
        CreateHardLink(liveDatabase, paths.Database);

        var error = Assert.Throws<InvalidOperationException>(
            () => MigrationValidationOptions.FromCommandLine(
                ValidArguments(paths),
                liveDatabase));

        Assert.Contains("shares filesystem identity", error.Message);
    }

    [Fact]
    public void Database_identity_change_after_preparation_is_rejected()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var paths = CreateValidationPaths();
        var arguments = ValidArguments(paths);
        ReplaceArgumentValue(
            arguments,
            MigrationValidationOptions.DatabaseInodeArgument,
            (paths.Identity.Inode + 1).ToString());

        var error = Assert.Throws<InvalidOperationException>(
            () => MigrationValidationOptions.FromCommandLine(
                arguments,
                CreateLiveDatabasePath()));

        Assert.Contains("identity changed", error.Message);
    }

    [Fact]
    public void Opened_connection_recheck_rejects_a_post_open_path_swap()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var paths = CreateValidationPaths();
        var liveDatabase = CreateLiveDatabasePath();
        using var connection =
            new SqliteConnection(MigrationValidationOptions.BuildConnectionString(paths.Database));
        connection.Open();

        var originalAtRenamedPath = Path.Combine(paths.Root, "database", "opened-original.db3");
        File.Move(paths.Database, originalAtRenamedPath);
        CreatePrivateFile(paths.Database);

        var error = Assert.Throws<InvalidOperationException>(() =>
            MigrationValidationOptions.VerifyOpenedDatabase(
                connection,
                paths.Root,
                paths.Database,
                liveDatabase,
                paths.Identity.Device,
                paths.Identity.Inode,
                GetEffectiveUserId()));

        Assert.Contains("identity does not match", error.Message);
    }

    [Theory]
    [InlineData("Data Source")]
    [InlineData("Mode=Memory;Data Source")]
    public void Connection_string_metacharacters_in_filename_cannot_redirect_main_database(
        string injectedPrefix)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var redirectName = $"redirected-{Guid.NewGuid():N}.db3";
        var paths = CreateValidationPaths(
            databaseFileName: $"sstudio.db3;{injectedPrefix}={redirectName}");
        var redirectedPath = Path.Combine(Environment.CurrentDirectory, redirectName);

        var options = MigrationValidationOptions.FromCommandLine(
            ValidArguments(paths),
            CreateLiveDatabasePath());
        Assert.NotNull(options);
        try
        {
            using var command = options.Connection.CreateCommand();
            command.CommandText =
                "SELECT file FROM pragma_database_list WHERE name = 'main';";
            var openedMain = Assert.IsType<string>(command.ExecuteScalar());

            Assert.Equal(
                paths.Database,
                MigrationValidationOptions.ResolvePhysicalPath(openedMain));
            Assert.Equal(paths.Database, options.Connection.DataSource);
            Assert.False(File.Exists(redirectedPath));
        }
        finally
        {
            options.Dispose();
            File.Delete(redirectedPath);
        }
    }

    [Fact]
    public void Group_accessible_validation_root_is_rejected()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var paths = CreateValidationPaths();
        File.SetUnixFileMode(
            paths.Root,
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead);

        var error = Assert.Throws<InvalidOperationException>(
            () => MigrationValidationOptions.FromCommandLine(
                ValidArguments(paths),
                CreateLiveDatabasePath()));

        Assert.Contains("current-user private directory", error.Message);
    }

    [Fact]
    public void Unknown_or_partial_validation_arguments_fail_closed()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var liveDatabase = CreateLiveDatabasePath();

        Assert.Throws<InvalidOperationException>(
            () => MigrationValidationOptions.FromCommandLine(
                new[] { "SentenceStudio", "--migration-validation-db", "/tmp/test.db3" },
                liveDatabase));

        Assert.Throws<InvalidOperationException>(
            () => MigrationValidationOptions.FromCommandLine(
                new[] { "SentenceStudio", "--migration-validation-unsafe" },
                liveDatabase));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1023")]
    [InlineData("65536")]
    [InlineData("not-a-port")]
    public void Unsafe_DevFlow_port_fails_closed(string port)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var arguments = ValidArguments(CreateValidationPaths());
        ReplaceArgumentValue(
            arguments,
            MigrationValidationOptions.DevFlowPortArgument,
            port);

        var error = Assert.Throws<InvalidOperationException>(
            () => MigrationValidationOptions.FromCommandLine(
                arguments,
                CreateLiveDatabasePath()));

        Assert.Contains("between 1024 and 65535", error.Message);
    }

    [Theory]
    [InlineData("abcd")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void Launch_token_must_be_256_bit_hexadecimal(string launchToken)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var arguments = ValidArguments(CreateValidationPaths());
        ReplaceArgumentValue(
            arguments,
            MigrationValidationOptions.LaunchTokenArgument,
            launchToken);

        var error = Assert.Throws<InvalidOperationException>(
            () => MigrationValidationOptions.FromCommandLine(
                arguments,
                CreateLiveDatabasePath()));

        Assert.Contains("exactly 256 bits", error.Message);
    }

    [Fact]
    public void Validation_parser_and_composition_are_debug_only()
    {
        var repoRoot = FindRepoRoot();
        var optionsSource = File.ReadAllText(
            Path.Combine(repoRoot, "src", "SentenceStudio.MacOS", "MigrationValidationOptions.cs"),
            Encoding.UTF8);
        var programSource = File.ReadAllText(
            Path.Combine(repoRoot, "src", "SentenceStudio.MacOS", "MacOSMauiProgram.cs"),
            Encoding.UTF8);
        var validationAppSource = File.ReadAllText(
            Path.Combine(repoRoot, "src", "SentenceStudio.MacOS", "MigrationValidationApp.cs"),
            Encoding.UTF8);
        var delegateSource = File.ReadAllText(
            Path.Combine(repoRoot, "src", "SentenceStudio.MacOS", "MauiMacOSApp.cs"),
            Encoding.UTF8);
        var mainSource = File.ReadAllText(
            Path.Combine(repoRoot, "src", "SentenceStudio.MacOS", "Main.cs"),
            Encoding.UTF8);
        var appBuilderSource = File.ReadAllText(
            Path.Combine(repoRoot, "src", "SentenceStudio.AppLib", "Setup", "SentenceStudioAppBuilder.cs"),
            Encoding.UTF8);

        Assert.StartsWith("#if DEBUG", optionsSource.TrimStart());
        Assert.EndsWith("#endif", optionsSource.TrimEnd());
        Assert.StartsWith("#if DEBUG", validationAppSource.TrimStart());
        Assert.EndsWith("#endif", validationAppSource.TrimEnd());
        Assert.Contains("#if DEBUG", programSource);
        Assert.Contains("MigrationValidationOptions.IsRequested(commandLineArguments)", programSource);
        Assert.Contains("MigrationValidationOptions.FromCommandLine(", programSource);
        Assert.Contains("builder.UseMauiAppMacOS<MigrationValidationApp>();", programSource);
        Assert.Contains(
            "builder.Services.AddSingleton<MigrationValidationOptions>(_ => migrationValidation);",
            programSource);
        Assert.Contains("builder.UseSentenceStudioApp(migrationValidation.Connection);", programSource);
        Assert.Contains("suppressAutomaticStartup: migrationValidation is not null", programSource);
        Assert.Contains(
            "Migration validation opened database binding:",
            programSource);
        Assert.Contains(
            "migrationValidation.OpenedBinding.FileDescriptor",
            programSource);
        Assert.DoesNotContain("ComponentType = typeof(Routes)", validationAppSource);
        Assert.Contains("GetService<MigrationValidationOptions>() is null", delegateSource);
        Assert.Contains("#if DEBUG", mainSource);
        Assert.Contains(
            "MigrationValidationOptions.BuildConnectionStringArgument",
            mainSource);
        Assert.Contains(
            "options.UseSqlite(validationConnection, contextOwnsConnection: false);",
            appBuilderSource);
        Assert.Contains(
            "ApplicationDbContext is not bound to the opened migration validation connection.",
            appBuilderSource);
        Assert.Contains("await dbContext.Database.MigrateAsync()", appBuilderSource);
        var migrateIndex = appBuilderSource.IndexOf(
            "await dbContext.Database.MigrateAsync()",
            StringComparison.Ordinal);
        var compatibilityPatchIndex = appBuilderSource.IndexOf(
            "await ApplyExistingMobileCompatibilityPatchesAsync(",
            StringComparison.Ordinal);
        var sanityIndex = appBuilderSource.IndexOf(
            "await ValidateSchemaAsync(_validationConnection)",
            StringComparison.Ordinal);
        Assert.True(migrateIndex < compatibilityPatchIndex);
        Assert.True(compatibilityPatchIndex < sanityIndex);
        Assert.Contains("MigrationValidationSyncService", appBuilderSource);
    }

    private string CreateLiveDatabasePath()
    {
        var liveDirectory = CreatePrivateDirectory(
            Path.Combine(_testRoot, $"live-{Guid.NewGuid():N}"));
        return Path.Combine(liveDirectory, "sstudio.db3");
    }

    private ValidationPaths CreateValidationPaths(
        string? requestedRoot = null,
        string databaseFileName = "sstudio.db3")
    {
        var root = CreatePrivateDirectory(
            requestedRoot ?? Path.Combine(_testRoot, $"validator-{Guid.NewGuid():N}"));
        var databaseDirectory = CreatePrivateDirectory(Path.Combine(root, "database"));
        var database = CreatePrivateFile(Path.Combine(databaseDirectory, databaseFileName));
        var token = Guid.NewGuid().ToString("N");
        var launchToken = Convert.ToHexString(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var marker = Path.Combine(root, MigrationValidationOptions.MarkerFileName);
        File.WriteAllText(marker, token, MarkerEncoding);
        if (OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                marker,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        var identity = MigrationValidationOptions.GetFileIdentity(database);
        return new ValidationPaths(root, database, token, launchToken, identity);
    }

    private static string[] ValidArguments(ValidationPaths paths) =>
    [
        "SentenceStudio",
        MigrationValidationOptions.ModeArgument,
        MigrationValidationOptions.RootArgument,
        paths.Root,
        MigrationValidationOptions.DatabaseArgument,
        paths.Database,
        MigrationValidationOptions.DatabaseDeviceArgument,
        paths.Identity.Device.ToString(),
        MigrationValidationOptions.DatabaseInodeArgument,
        paths.Identity.Inode.ToString(),
        MigrationValidationOptions.TokenArgument,
        paths.Token,
        MigrationValidationOptions.LaunchTokenArgument,
        paths.LaunchToken,
        MigrationValidationOptions.DevFlowPortArgument,
        "19225",
        MigrationValidationOptions.DisableSyncArgument,
    ];

    private static void ReplaceArgumentValue(string[] arguments, string name, string value)
    {
        var valueIndex = Array.IndexOf(arguments, name) + 1;
        Assert.True(valueIndex > 0);
        arguments[valueIndex] = value;
    }

    private static string CreatePrivateDirectory(string path)
    {
        var directory = Directory.CreateDirectory(path).FullName;
        SetPrivateDirectoryMode(directory);
        return MigrationValidationOptions.ResolvePhysicalPath(directory);
    }

    private static string CreatePrivateFile(string path)
    {
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
        }

        if (OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        return MigrationValidationOptions.ResolvePhysicalPath(path);
    }

    private static void SetPrivateDirectoryMode(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        }
    }

    private static void CreateHardLink(string linkPath, string targetPath)
    {
        if (NativeMethods.Link(targetPath, linkPath) != 0)
        {
            throw new InvalidOperationException(
                "Could not create hard-link test fixture.",
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    private static uint GetEffectiveUserId() => NativeMethods.GetEffectiveUserId();

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "SentenceStudio.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root.");
    }

    public void Dispose()
    {
        Directory.Delete(_testRoot, recursive: true);
    }

    private sealed record ValidationPaths(
        string Root,
        string Database,
        string Token,
        string LaunchToken,
        MigrationValidationOptions.FileIdentity Identity);

    private static class NativeMethods
    {
        [DllImport("libSystem.B.dylib", EntryPoint = "link", SetLastError = true)]
        internal static extern int Link(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string targetPath,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string linkPath);

        [DllImport("libSystem.B.dylib", EntryPoint = "geteuid")]
        internal static extern uint GetEffectiveUserId();
    }
}
#endif
