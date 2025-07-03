namespace SentenceStudio.Common;

public static class Constants
{
    public const string DatabaseFilename = "sstudio.db3";

    public static string DatabasePath =>
        System.IO.Path.Combine(FileSystem.AppDataDirectory, DatabaseFilename);

    public static string SQLDatabasePath =>
        $"Data Source={System.IO.Path.Combine(FileSystem.AppDataDirectory, DatabaseFilename)}";

    public static string[] MediaTypes = new[] { "Video", "Podcast", "Image", "Vocabulary List", "Article", "Other" };
    public static string[] Languages = new[] { "English", "Korean", "Spanish", "Japanese", "Chinese", "French", "German", "Italian", "Portuguese", "Russian" };

    public static double PixelsPerSecond = 120;
    public static float Amplitude = 1.2f;
}