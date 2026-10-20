namespace SentenceStudio.Native.Android;

internal sealed class AndroidQuizPhotoTransform
{
    private float _viewportWidth;
    private float _viewportHeight;

    public float Scale { get; private set; } = 1f;
    public float OffsetX { get; private set; }
    public float OffsetY { get; private set; }
    public float ImageWidth { get; private set; }
    public float ImageHeight { get; private set; }

    public void SetViewport(float width, float height, int photoWidth, int photoHeight)
    {
        if (!float.IsFinite(width) || !float.IsFinite(height)
            || width <= 0 || height <= 0 || photoWidth <= 0 || photoHeight <= 0)
            return;
        var fit = Math.Min(width / photoWidth, height / photoHeight);
        var imageWidth = photoWidth * fit;
        var imageHeight = photoHeight * fit;
        if (_viewportWidth == width && _viewportHeight == height
            && ImageWidth == imageWidth && ImageHeight == imageHeight)
            return;
        _viewportWidth = width;
        _viewportHeight = height;
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
        ResetZoom();
    }

    public bool ContainsImage(float x, float y)
    {
        if (_viewportWidth <= 0 || _viewportHeight <= 0
            || !float.IsFinite(x) || !float.IsFinite(y))
            return false;
        var halfWidth = ImageWidth * Scale / 2f;
        var halfHeight = ImageHeight * Scale / 2f;
        return Math.Abs(x - _viewportWidth / 2f - OffsetX) <= halfWidth
            && Math.Abs(y - _viewportHeight / 2f - OffsetY) <= halfHeight;
    }

    public void ZoomAndPan(float focusX, float focusY, float panX, float panY,
        float zoom)
    {
        if (_viewportWidth <= 0 || _viewportHeight <= 0
            || !float.IsFinite(focusX) || !float.IsFinite(focusY)
            || !float.IsFinite(panX) || !float.IsFinite(panY)
            || !float.IsFinite(zoom) || zoom <= 0)
            return;
        var next = Math.Clamp(Scale * zoom, 1f, 4f);
        var ratio = next / Scale;
        OffsetX = OffsetX * ratio
            + (focusX - _viewportWidth / 2f) * (1f - ratio) + panX;
        OffsetY = OffsetY * ratio
            + (focusY - _viewportHeight / 2f) * (1f - ratio) + panY;
        Scale = next;
        OffsetX = Math.Clamp(OffsetX,
            -Math.Max(0, (ImageWidth * Scale - _viewportWidth) / 2f),
            Math.Max(0, (ImageWidth * Scale - _viewportWidth) / 2f));
        OffsetY = Math.Clamp(OffsetY,
            -Math.Max(0, (ImageHeight * Scale - _viewportHeight) / 2f),
            Math.Max(0, (ImageHeight * Scale - _viewportHeight) / 2f));
    }

    public void DoubleTap(float x, float y)
    {
        if (Scale > 1f)
            ResetZoom();
        else
            ZoomAndPan(x, y, 0, 0, 2f);
    }

    private void ResetZoom()
    {
        Scale = 1f;
        OffsetX = 0;
        OffsetY = 0;
    }
}
