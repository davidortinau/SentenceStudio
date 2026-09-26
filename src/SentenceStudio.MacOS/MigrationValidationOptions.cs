#if DEBUG
using System.Data;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Data.Sqlite;

namespace SentenceStudio.MacOS;

/// <summary>
/// Parses and consumes the explicit, Debug-only contract used by the native migration validator.
/// Validation happens before the database path is registered with Entity Framework.
/// </summary>
internal sealed class MigrationValidationOptions : IDisposable
{
    internal const string ModeArgument = "--migration-validation";
    internal const string RootArgument = "--migration-validation-root";
    internal const string DatabaseArgument = "--migration-validation-db";
    internal const string DatabaseDeviceArgument = "--migration-validation-db-device";
    internal const string DatabaseInodeArgument = "--migration-validation-db-inode";
    internal const string TokenArgument = "--migration-validation-token";
    internal const string LaunchTokenArgument = "--migration-validation-launch-token";
    internal const string DevFlowPortArgument = "--migration-validation-devflow-port";
    internal const string DisableSyncArgument = "--migration-validation-disable-sync";
    internal const string BuildConnectionStringArgument =
        "--migration-validation-build-connection-string";
    internal const string MarkerFileName = ".sentencestudio-migration-validation";
    internal const string ConsumedMarkerFileName = ".sentencestudio-migration-validation.consumed";

    private const uint FileTypeMask = 0xF000;
    private const uint RegularFileType = 0x8000;
    private const uint DirectoryType = 0x4000;
    private const uint SymbolicLinkType = 0xA000;
    private const uint PermissionMask = 0x1FF;
    private const uint Mode600 = 0x180;
    private const uint Mode700 = 0x1C0;
    private const int ProcPidFdVnodePathInfo = 2;
    private const int VnodeFdInfoSize = 1200;
    private const int VnodeStatOffset = 24;
    private const int VnodePathOffset = 176;

    private bool _disposed;

    private MigrationValidationOptions(
        string rootPath,
        string databasePath,
        int devFlowPort,
        string launchToken,
        SqliteConnection connection,
        OpenedDatabaseBinding openedDatabaseBinding)
    {
        RootPath = rootPath;
        DatabasePath = databasePath;
        DevFlowPort = devFlowPort;
        LaunchToken = launchToken;
        Connection = connection;
        OpenedBinding = openedDatabaseBinding;
    }

    internal string RootPath { get; }

    internal string DatabasePath { get; }

    internal int DevFlowPort { get; }

    internal string LaunchToken { get; }

    internal SqliteConnection Connection { get; }

    internal OpenedDatabaseBinding OpenedBinding { get; }

    internal FileIdentity OpenedDatabaseIdentity => OpenedBinding.Identity;

    internal static bool IsRequested(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Any(argument =>
            string.Equals(argument, ModeArgument, StringComparison.Ordinal));
    }

    internal static MigrationValidationOptions? FromCommandLine(
        IReadOnlyList<string> arguments,
        string liveDatabasePath)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var validationArguments = arguments
            .Where(argument => argument.StartsWith("--migration-validation", StringComparison.Ordinal))
            .ToArray();

        if (validationArguments.Length == 0)
        {
            return null;
        }

        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "Native migration validation is supported only on macOS.");
        }

        RequireSingleFlag(arguments, ModeArgument);
        RequireSingleFlag(arguments, DisableSyncArgument);
        RejectUnknownValidationArguments(validationArguments);

        var root = ReadSingleValue(arguments, RootArgument);
        var database = ReadSingleValue(arguments, DatabaseArgument);
        var token = ReadSingleValue(arguments, TokenArgument);
        var launchToken = ReadSingleValue(arguments, LaunchTokenArgument);
        var expectedDeviceValue = ReadSingleValue(arguments, DatabaseDeviceArgument);
        var expectedInodeValue = ReadSingleValue(arguments, DatabaseInodeArgument);
        var portValue = ReadSingleValue(arguments, DevFlowPortArgument);

        if (launchToken.Length != 64 || launchToken.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new InvalidOperationException(
                "Migration validation launch token must be exactly 256 bits encoded as hexadecimal.");
        }

        if (!long.TryParse(expectedDeviceValue, out var expectedDevice)
            || expectedDevice < 0
            || !ulong.TryParse(expectedInodeValue, out var expectedInode)
            || expectedInode == 0)
        {
            throw new InvalidOperationException(
                "Migration validation database identity arguments are invalid.");
        }

        if (!int.TryParse(portValue, out var devFlowPort)
            || devFlowPort is < 1024 or > 65535)
        {
            throw new InvalidOperationException(
                "Migration validation DevFlow port must be between 1024 and 65535.");
        }

        if (!Path.IsPathFullyQualified(root)
            || !Path.IsPathFullyQualified(database)
            || !Path.IsPathFullyQualified(liveDatabasePath))
        {
            throw new InvalidOperationException(
                "Migration validation root, database, and live database paths must be absolute.");
        }

        RejectSymbolicLinkAncestors(root, requireLeaf: true);
        RejectSymbolicLinkAncestors(database, requireLeaf: true);

        var canonicalTempRoot = ResolvePhysicalPath(Path.GetTempPath());
        var canonicalRoot = ResolvePhysicalPath(root);
        var canonicalDatabase = ResolvePhysicalPath(database);
        var canonicalLiveDatabase = ResolvePhysicalPathAllowMissingLeaf(liveDatabasePath);
        var canonicalLiveDataDirectory = Path.GetDirectoryName(canonicalLiveDatabase)
            ?? throw new InvalidOperationException("The live database path has no parent directory.");

        var effectiveUserId = NativeMethods.GetEffectiveUserId();
        var tempRootIdentity = GetFileIdentity(canonicalTempRoot);
        if (!tempRootIdentity.IsDirectory
            || tempRootIdentity.UserId != effectiveUserId
            || (tempRootIdentity.Mode & PermissionMask) != Mode700)
        {
            throw new InvalidOperationException(
                "The physical temporary directory must be current-user owned with mode 700.");
        }

        if (PathsEqual(canonicalRoot, canonicalTempRoot)
            || !IsWithin(canonicalRoot, canonicalTempRoot))
        {
            throw new InvalidOperationException(
                "Migration validation root must be a private child of the physical temporary directory.");
        }

        if (PathsEqual(canonicalRoot, canonicalDatabase)
            || !IsWithin(canonicalDatabase, canonicalRoot))
        {
            throw new InvalidOperationException(
                "Migration validation database must be a file inside the validation root.");
        }

        if (PathsEqual(canonicalRoot, canonicalLiveDataDirectory)
            || IsWithin(canonicalRoot, canonicalLiveDataDirectory)
            || IsWithinOrEqual(canonicalDatabase, canonicalLiveDataDirectory))
        {
            throw new InvalidOperationException(
                "Migration validation paths must not use the live application data directory.");
        }

        var databaseDirectory = Path.GetDirectoryName(canonicalDatabase);
        if (databaseDirectory is null
            || !Directory.Exists(databaseDirectory)
            || !IsWithinOrEqual(databaseDirectory, canonicalRoot))
        {
            throw new InvalidOperationException(
                "Migration validation database parent must exist inside the validation root.");
        }

        var rootIdentity = GetFileIdentity(canonicalRoot);
        if (!rootIdentity.IsDirectory
            || rootIdentity.UserId != effectiveUserId
            || (rootIdentity.Mode & PermissionMask) != Mode700)
        {
            throw new InvalidOperationException(
                "Migration validation root must be a current-user private directory.");
        }

        var databaseIdentity = GetFileIdentity(canonicalDatabase);
        if (!databaseIdentity.IsRegularFile
            || databaseIdentity.UserId != effectiveUserId
            || (databaseIdentity.Mode & PermissionMask) != Mode600)
        {
            throw new InvalidOperationException(
                "Migration validation database must be a current-user mode-600 regular file.");
        }

        if (databaseIdentity.Device != expectedDevice
            || databaseIdentity.Inode != expectedInode)
        {
            throw new InvalidOperationException(
                "Migration validation database identity changed after preparation.");
        }

        if (PathsEqual(canonicalDatabase, canonicalLiveDatabase))
        {
            throw new InvalidOperationException(
                "Migration validation database must not be the live application database.");
        }

        if (File.Exists(canonicalLiveDatabase))
        {
            var liveIdentity = GetFileIdentity(canonicalLiveDatabase);
            if (databaseIdentity.Device == liveIdentity.Device
                && databaseIdentity.Inode == liveIdentity.Inode)
            {
                throw new InvalidOperationException(
                    "Migration validation database shares filesystem identity with the live database.");
            }
        }

        if (databaseIdentity.LinkCount != 1)
        {
            throw new InvalidOperationException(
                    "Migration validation database must have link count one.");
        }

        ConsumeMarker(canonicalRoot, token, effectiveUserId);

        var connection = new SqliteConnection(BuildConnectionString(canonicalDatabase));
        try
        {
            connection.Open();
            var openedBinding = VerifyOpenedDatabase(
                connection,
                canonicalRoot,
                canonicalDatabase,
                canonicalLiveDatabase,
                expectedDevice,
                expectedInode,
                effectiveUserId);

            return new MigrationValidationOptions(
                canonicalRoot,
                canonicalDatabase,
                devFlowPort,
                launchToken,
                connection,
                openedBinding);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    internal static string BuildConnectionString(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)
            || !Path.IsPathFullyQualified(databasePath))
        {
            throw new ArgumentException(
                "Migration validation database path must be absolute.",
                nameof(databasePath));
        }

        return new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();
    }

    internal static OpenedDatabaseBinding VerifyOpenedDatabase(
        SqliteConnection connection,
        string canonicalRoot,
        string canonicalDatabase,
        string canonicalLiveDatabase,
        long expectedDevice,
        ulong expectedInode,
        uint effectiveUserId)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException(
                "Migration validation database connection must already be open.");
        }

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT file FROM pragma_database_list WHERE name = 'main';";
        var openedMainPath = command.ExecuteScalar()?.ToString();
        if (string.IsNullOrWhiteSpace(openedMainPath)
            || !Path.IsPathFullyQualified(openedMainPath))
        {
            throw new InvalidOperationException(
                "SQLite did not report an absolute main database path.");
        }

        RejectSymbolicLinkAncestors(canonicalRoot, requireLeaf: true);
        RejectSymbolicLinkAncestors(canonicalDatabase, requireLeaf: true);
        RejectSymbolicLinkAncestors(openedMainPath, requireLeaf: true);

        var physicalOpenedMain = ResolvePhysicalPath(openedMainPath);
        if (!PathsEqual(physicalOpenedMain, canonicalDatabase))
        {
            throw new InvalidOperationException(
                "SQLite opened main database path does not match the validated disposable database.");
        }

        var databaseParent = Path.GetDirectoryName(canonicalDatabase)
            ?? throw new InvalidOperationException(
                "Migration validation database has no parent directory.");
        var rootIdentity = GetFileIdentity(canonicalRoot);
        var parentIdentity = GetFileIdentity(databaseParent);
        var databaseIdentity = GetFileIdentity(physicalOpenedMain);

        if (!rootIdentity.IsDirectory
            || rootIdentity.UserId != effectiveUserId
            || (rootIdentity.Mode & PermissionMask) != Mode700
            || !parentIdentity.IsDirectory
            || parentIdentity.UserId != effectiveUserId
            || (parentIdentity.Mode & PermissionMask) != Mode700)
        {
            throw new InvalidOperationException(
                "Migration validation root and database parent changed after SQLite opened.");
        }

        if (!databaseIdentity.IsRegularFile
            || databaseIdentity.UserId != effectiveUserId
            || (databaseIdentity.Mode & PermissionMask) != Mode600
            || databaseIdentity.LinkCount != 1)
        {
            throw new InvalidOperationException(
                "Opened migration validation database must remain a current-user mode-600 regular file with link count one.");
        }

        if (databaseIdentity.Device != expectedDevice
            || databaseIdentity.Inode != expectedInode)
        {
            throw new InvalidOperationException(
                "SQLite opened main database identity does not match the validated disposable inode.");
        }

        if (File.Exists(canonicalLiveDatabase))
        {
            RejectSymbolicLinkAncestors(canonicalLiveDatabase, requireLeaf: true);
            var liveIdentity = GetFileIdentity(canonicalLiveDatabase);
            if (databaseIdentity.Device == liveIdentity.Device
                && databaseIdentity.Inode == liveIdentity.Inode)
            {
                throw new InvalidOperationException(
                    "SQLite opened main database shares filesystem identity with the live database.");
            }
        }

        var openedFileDescriptor = VerifyOpenFileDescriptorBinding(
            physicalOpenedMain,
            databaseIdentity);

        return new OpenedDatabaseBinding(
            physicalOpenedMain,
            openedFileDescriptor,
            databaseIdentity);
    }

    private static int VerifyOpenFileDescriptorBinding(
        string expectedPath,
        FileIdentity expectedIdentity)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "Opened database descriptor validation requires macOS.");
        }

        var matchingDescriptors = new List<int>();
        var descriptors = Directory.EnumerateFileSystemEntries("/dev/fd")
            .Select(Path.GetFileName)
            .Select(name => int.TryParse(name, out var descriptor) ? descriptor : -1)
            .Where(descriptor => descriptor >= 0)
            .Distinct()
            .ToArray();
        var descriptorInfo = Marshal.AllocHGlobal(VnodeFdInfoSize);
        try
        {
            foreach (var descriptor in descriptors)
            {
                var bytesWritten = NativeMethods.ProcPidFdInfo(
                    Environment.ProcessId,
                    descriptor,
                    ProcPidFdVnodePathInfo,
                    descriptorInfo,
                    VnodeFdInfoSize);
                if (bytesWritten < VnodeFdInfoSize)
                {
                    continue;
                }

                var descriptorDevice = unchecked(
                    (uint)Marshal.ReadInt32(descriptorInfo, VnodeStatOffset));
                var descriptorMode = unchecked(
                    (ushort)Marshal.ReadInt16(descriptorInfo, VnodeStatOffset + 4));
                var descriptorLinkCount = unchecked(
                    (ushort)Marshal.ReadInt16(descriptorInfo, VnodeStatOffset + 6));
                var descriptorInode = unchecked(
                    (ulong)Marshal.ReadInt64(descriptorInfo, VnodeStatOffset + 8));
                var descriptorUserId = unchecked(
                    (uint)Marshal.ReadInt32(descriptorInfo, VnodeStatOffset + 16));
                var descriptorPath = Marshal.PtrToStringUTF8(
                    IntPtr.Add(descriptorInfo, VnodePathOffset));

                if (descriptorDevice == expectedIdentity.Device
                    && descriptorInode == expectedIdentity.Inode
                    && descriptorUserId == expectedIdentity.UserId
                    && descriptorLinkCount == expectedIdentity.LinkCount
                    && descriptorMode == expectedIdentity.Mode
                    && (descriptorMode & FileTypeMask) == RegularFileType
                    && !string.IsNullOrWhiteSpace(descriptorPath)
                    && PathsEqual(descriptorPath, expectedPath))
                {
                    matchingDescriptors.Add(descriptor);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(descriptorInfo);
        }

        if (matchingDescriptors.Count != 1)
        {
            throw new InvalidOperationException(
                $"Expected exactly one open SQLite main descriptor for the validated disposable inode, found {matchingDescriptors.Count}.");
        }

        return matchingDescriptors[0];
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Connection.Dispose();
    }

    internal static FileIdentity GetFileIdentity(string path)
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException(
                "Filesystem identity validation requires macOS.");
        }

        if (NativeMethods.LStat(path, out var stat) != 0)
        {
            throw new InvalidOperationException(
                $"Could not inspect migration validation path: {path}",
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        }

        return new FileIdentity(
            stat.Device,
            stat.Inode,
            stat.UserId,
            stat.LinkCount,
            stat.Mode,
            (stat.Mode & FileTypeMask) == RegularFileType,
            (stat.Mode & FileTypeMask) == DirectoryType,
            (stat.Mode & FileTypeMask) == SymbolicLinkType);
    }

    internal static string ResolvePhysicalPath(string path)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return Path.GetFullPath(path);
        }

        var resolved = NativeMethods.RealPath(path, IntPtr.Zero);
        if (resolved == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"Could not resolve physical migration validation path: {path}",
                new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
        }

        try
        {
            return Path.TrimEndingDirectorySeparator(
                Marshal.PtrToStringUTF8(resolved)
                ?? throw new InvalidOperationException("realpath returned an empty path."));
        }
        finally
        {
            NativeMethods.Free(resolved);
        }
    }

    private static string ResolvePhysicalPathAllowMissingLeaf(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            return ResolvePhysicalPath(path);
        }

        var parent = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The live database path has no parent directory.");
        return Path.Combine(ResolvePhysicalPath(parent), Path.GetFileName(path));
    }

    private static void ConsumeMarker(string rootPath, string token, uint effectiveUserId)
    {
        var markerPath = Path.Combine(rootPath, MarkerFileName);
        var consumedMarkerPath = Path.Combine(rootPath, ConsumedMarkerFileName);

        RejectSymbolicLinkAncestors(markerPath, requireLeaf: true);
        if (File.Exists(consumedMarkerPath) || Directory.Exists(consumedMarkerPath))
        {
            throw new InvalidOperationException(
                "Migration validation marker was already consumed.");
        }

        var markerIdentity = GetFileIdentity(markerPath);
        if (!markerIdentity.IsRegularFile
            || markerIdentity.UserId != effectiveUserId
            || markerIdentity.LinkCount != 1
            || (markerIdentity.Mode & PermissionMask) != Mode600)
        {
            throw new InvalidOperationException(
                "Migration validation marker must be a current-user mode-600 regular file.");
        }

        try
        {
            // A same-directory rename is atomic. Once it succeeds, a replay using the same
            // command line cannot find the marker, including if this process fails afterward.
            File.Move(markerPath, consumedMarkerPath, overwrite: false);

            var consumedIdentity = GetFileIdentity(consumedMarkerPath);
            if (consumedIdentity.Device != markerIdentity.Device
                || consumedIdentity.Inode != markerIdentity.Inode)
            {
                throw new InvalidOperationException(
                    "Migration validation marker identity changed while it was consumed.");
            }

            using var stream = new FileStream(
                consumedMarkerPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.None);
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: false);
            var markerToken = reader.ReadToEnd().Trim();
            if (string.IsNullOrWhiteSpace(token)
                || !string.Equals(markerToken, token, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Migration validation marker token does not match.");
            }
        }
        finally
        {
            File.Delete(consumedMarkerPath);
        }
    }

    private static void RejectUnknownValidationArguments(IEnumerable<string> validationArguments)
    {
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            ModeArgument,
            RootArgument,
            DatabaseArgument,
            DatabaseDeviceArgument,
            DatabaseInodeArgument,
            TokenArgument,
            LaunchTokenArgument,
            DevFlowPortArgument,
            DisableSyncArgument,
        };

        foreach (var argument in validationArguments)
        {
            if (!known.Contains(argument))
            {
                throw new InvalidOperationException(
                    $"Unknown migration validation argument: {argument}");
            }
        }
    }

    private static string ReadSingleValue(IReadOnlyList<string> arguments, string name)
    {
        var indexes = Enumerable.Range(0, arguments.Count)
            .Where(index => string.Equals(arguments[index], name, StringComparison.Ordinal))
            .ToArray();

        if (indexes.Length != 1)
        {
            throw new InvalidOperationException(
                $"Migration validation requires exactly one {name} argument.");
        }

        var valueIndex = indexes[0] + 1;
        if (valueIndex >= arguments.Count
            || arguments[valueIndex].StartsWith("--migration-validation", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(arguments[valueIndex]))
        {
            throw new InvalidOperationException(
                $"Migration validation argument {name} requires one value.");
        }

        return arguments[valueIndex];
    }

    private static void RequireSingleFlag(IReadOnlyList<string> arguments, string name)
    {
        if (arguments.Count(argument => string.Equals(argument, name, StringComparison.Ordinal)) != 1)
        {
            throw new InvalidOperationException(
                $"Migration validation requires exactly one {name} flag.");
        }
    }

    private static void RejectSymbolicLinkAncestors(string path, bool requireLeaf)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)
            ?? throw new InvalidOperationException("Migration validation path has no filesystem root.");
        var relative = fullPath[root.Length..];
        var current = Path.TrimEndingDirectorySeparator(root);

        foreach (var component in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                if (requireLeaf || !PathsEqual(current, fullPath))
                {
                    throw new InvalidOperationException(
                        $"Migration validation path component does not exist: {current}");
                }

                continue;
            }

            if (GetFileIdentity(current).IsSymbolicLink)
            {
                throw new InvalidOperationException(
                    "Migration validation paths must not contain symbolic links.");
            }
        }
    }

    private static bool IsWithin(string candidate, string directory)
    {
        var relative = Path.GetRelativePath(directory, candidate);
        return !PathsEqual(relative, ".")
            && !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool IsWithinOrEqual(string candidate, string directory) =>
        PathsEqual(candidate, directory) || IsWithin(candidate, directory);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.Ordinal);

    internal readonly record struct FileIdentity(
        long Device,
        ulong Inode,
        uint UserId,
        uint LinkCount,
        uint Mode,
        bool IsRegularFile,
        bool IsDirectory,
        bool IsSymbolicLink);

    internal readonly record struct OpenedDatabaseBinding(
        string Path,
        int FileDescriptor,
        FileIdentity Identity);

    private static class NativeMethods
    {
        [DllImport("libSystem.B.dylib", EntryPoint = "lstat", SetLastError = true)]
        internal static extern int LStat(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            out DarwinStat stat);

        [DllImport("libproc.dylib", EntryPoint = "proc_pidfdinfo", SetLastError = true)]
        internal static extern int ProcPidFdInfo(
            int processId,
            int fileDescriptor,
            int flavor,
            IntPtr buffer,
            int bufferSize);

        [DllImport("libSystem.B.dylib", EntryPoint = "realpath", SetLastError = true)]
        internal static extern IntPtr RealPath(
            [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
            IntPtr resolvedPath);

        [DllImport("libSystem.B.dylib", EntryPoint = "free")]
        internal static extern void Free(IntPtr pointer);

        [DllImport("libSystem.B.dylib", EntryPoint = "geteuid")]
        internal static extern uint GetEffectiveUserId();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinTimespec
    {
        internal long Seconds;
        internal long Nanoseconds;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DarwinStat
    {
        internal int Device;
        internal ushort Mode;
        internal ushort LinkCount;
        internal ulong Inode;
        internal uint UserId;
        internal uint GroupId;
        internal int RDevice;
        internal DarwinTimespec AccessTime;
        internal DarwinTimespec ModificationTime;
        internal DarwinTimespec ChangeTime;
        internal DarwinTimespec BirthTime;
        internal long Size;
        internal long Blocks;
        internal int BlockSize;
        internal uint Flags;
        internal uint Generation;
        internal int Spare;
        internal long Spare1;
        internal long Spare2;
    }
}
#endif
