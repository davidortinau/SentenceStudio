namespace SentenceStudio.Common;

public static class Constants
{
    public const string DatabaseFilename = "sstudio.db3";

    public const SQLite.SQLiteOpenFlags Flags =
        // open the database in read/write mode
        SQLite.SQLiteOpenFlags.ReadWrite |
        // create the database if it doesn't exist
        SQLite.SQLiteOpenFlags.Create |
        // enable multi-threaded database access
        SQLite.SQLiteOpenFlags.SharedCache;

    public static string DatabasePath => 
        System.IO.Path.Combine(FileSystem.AppDataDirectory, DatabaseFilename);

    public static string SQLDatabasePath =>
		$"Data Source={System.IO.Path.Combine(FileSystem.AppDataDirectory, DatabaseFilename)}";

    public static string LocalhostUrl = DeviceInfo.Platform == DevicePlatform.Android ? "10.0.2.2" : "localhost";
    public static string Scheme = "https"; // or http
    public static string Port = "7179";
    public static string RestUrl = $"{Scheme}://{LocalhostUrl}:{Port}";//tables/todoitems/{{0}}
}