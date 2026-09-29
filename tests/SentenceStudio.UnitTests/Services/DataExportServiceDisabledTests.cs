using SentenceStudio.Services;

namespace SentenceStudio.UnitTests.Services;

public sealed class DataExportServiceDisabledTests
{
    [Fact]
    public async Task Legacy_export_fails_before_reading_data_or_reporting_progress()
    {
        var progress = new RecordingProgress();
        var service = new DataExportService(new ThrowOnResolveServiceProvider());

        var error = await Assert.ThrowsAsync<NotSupportedException>(
            () => service.ExportAllDataAsZipAsync(progress));

        Assert.Contains("disabled", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, progress.ReportCount);
    }

    private sealed class ThrowOnResolveServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            throw new InvalidOperationException("Disabled export must not resolve a database.");
    }

    private sealed class RecordingProgress : IProgress<string>
    {
        public int ReportCount { get; private set; }

        public void Report(string value) => ReportCount++;
    }
}
