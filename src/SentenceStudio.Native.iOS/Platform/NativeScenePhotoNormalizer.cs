using Foundation;
using ImageIO;

namespace SentenceStudio.Native.iOS.Platform;

/// <summary>
/// Non-UI ImageIO step for a photo chosen in the SwiftUI PhotosPicker: the
/// picker can hand back HEIC images up to 25 MB, while the scene gallery
/// stores JPEG/PNG under the 8 MB image-view limit. Decodes with ImageIO,
/// applies the EXIF orientation, limits the long edge to 2048 pixels and
/// re-encodes as JPEG. No UIKit is used.
/// </summary>
internal static class NativeScenePhotoNormalizer
{
    internal const int MaxPixelSize = 2048;

    internal static NativeScenePickedPhoto? Normalize(byte[] data)
    {
        if (data is not { Length: > 0 })
            return null;
        using var source = CGImageSource.FromData(NSData.FromArray(data));
        if (source is null || source.ImageCount < 1)
            return null;
        using var image = source.CreateThumbnail(0, new CGImageThumbnailOptions
        {
            CreateThumbnailFromImageAlways = true,
            CreateThumbnailWithTransform = true,
            MaxPixelSize = MaxPixelSize
        });
        if (image is null)
            return null;
        using var output = new NSMutableData();
        using var destination = CGImageDestination.Create(output, "public.jpeg", 1);
        if (destination is null)
            return null;
        destination.AddImage(image, new CGImageDestinationOptions { LossyCompressionQuality = 0.85f });
        if (!destination.Close())
            return null;
        var bytes = output.ToArray();
        return bytes.Length is > 0 and <= NativeSceneImageStore.MaxImageBytes
            ? new NativeScenePickedPhoto(bytes, "image/jpeg")
            : null;
    }
}
