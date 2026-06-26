using Foundation;
using Social;
using UIKit;
using SentenceStudio.Sharing;

namespace SentenceStudio.ShareExtension;

public partial class ShareViewController : SLComposeServiceViewController
{
    private const string PlainTextUti = "public.plain-text";
    private const string UrlUti = "public.url";

    protected ShareViewController(IntPtr handle) : base(handle)
    {
    }

    public override bool IsContentValid() => true;

    public override void DidSelectPost() => CaptureAndComplete();

    private async void CaptureAndComplete()
    {
        try
        {
            var dir = ResolveQueueDirectory();
            var items = await CaptureItemsAsync();

            if (dir is not null && items.Count > 0)
            {
                var queue = new FileSystemSharedIngestQueue(dir);
                foreach (var item in items)
                    queue.Enqueue(item);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ShareExtension] capture failed: {ex}");
        }
        finally
        {
            ExtensionContext?.CompleteRequest(Array.Empty<NSExtensionItem>(), null);
        }
    }


    private string? ResolveQueueDirectory()
    {
        var container = NSFileManager.DefaultManager.GetContainerUrl(SharingConstants.AppGroupId);
        var dir = container?.Append(SharingConstants.QueueDirectoryName, true);
        return dir?.Path;
    }

    private async Task<List<SharedIngestItem>> CaptureItemsAsync()
    {
        var results = new List<SharedIngestItem>();
        var inputItems = ExtensionContext?.InputItems ?? Array.Empty<NSExtensionItem>();
        foreach (var input in inputItems)
        {
            var attachments = input.Attachments ?? Array.Empty<NSItemProvider>();
            foreach (var provider in attachments)
            {
                if (provider.HasItemConformingTo(PlainTextUti))
                {
                    var loaded = await provider.LoadItemAsync(PlainTextUti, null);
                    var text = (loaded as NSString)?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        results.Add(new SharedIngestItem
                        {
                            Kind = SharedIngestKind.Text,
                            Payload = text!,
                            CapturedAtUtc = DateTime.UtcNow
                        });
                    }
                }
                else if (provider.HasItemConformingTo(UrlUti))
                {
                    var loaded = await provider.LoadItemAsync(UrlUti, null);
                    var url = (loaded as NSUrl)?.AbsoluteString;
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        results.Add(new SharedIngestItem
                        {
                            Kind = SharedIngestKind.Url,
                            Payload = url!,
                            CapturedAtUtc = DateTime.UtcNow
                        });
                    }
                }
            }
        }

        // Fall back to the compose sheet text if nothing else was captured.
        if (results.Count == 0)
        {
            var typed = ContentText?.ToString();
            if (!string.IsNullOrWhiteSpace(typed))
            {
                results.Add(new SharedIngestItem
                {
                    Kind = SharedIngestKind.Text,
                    Payload = typed!,
                    CapturedAtUtc = DateTime.UtcNow
                });
            }
        }

        return results;
    }

    public override SLComposeSheetConfigurationItem[] GetConfigurationItems()
        => Array.Empty<SLComposeSheetConfigurationItem>();
}
