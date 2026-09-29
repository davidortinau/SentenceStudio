namespace SentenceStudio.Services;

public sealed class DataExportService
{
    public DataExportService(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
    }

    public Task<MemoryStream> ExportAllDataAsZipAsync(IProgress<string>? progress = null)
    {
        throw new NotSupportedException(
            "Profile and Settings data export is disabled until an owner-scoped export is implemented.");
    }
}
