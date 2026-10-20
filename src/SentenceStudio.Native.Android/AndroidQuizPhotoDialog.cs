using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using AndroidX.Compose.UI.Graphics;
using AndroidX.Compose.UI.Graphics.Painter;
using Bitmap = Android.Graphics.Bitmap;

namespace SentenceStudio.Native.Android;

internal sealed class AndroidQuizPhotoDialog : IDisposable
{
    private readonly MainActivity _activity;
    private readonly Bitmap _bitmap;
    private readonly Painter _painter;
    private readonly string _closeLabel;
    private readonly string _imageLabel;
    private readonly string _title;
    private readonly Action _onDismiss;
    private readonly FocusRequester _closeFocus = new();
    private readonly AndroidQuizPhotoTransform _transform = new();
    private readonly MutableState<int> _gestureRevision = new(0);
    private bool _disposed;

    public AndroidQuizPhotoDialog(
        MainActivity activity, Bitmap bitmap, string ownerId, string turnId,
        string photoUri, string closeLabel, string imageLabel,
        string dialogTitle, Action onDismiss)
    {
        _activity = activity;
        _bitmap = bitmap;
        OwnerId = ownerId;
        TurnId = turnId;
        PhotoUri = photoUri;
        _closeLabel = closeLabel;
        _imageLabel = imageLabel;
        _title = dialogTitle;
        _onDismiss = onDismiss;
        var imageBitmap = AndroidImageBitmap_androidKt.AsImageBitmap(bitmap);
        _painter = BitmapPainterKt.BitmapPainter(
            image: imageBitmap, srcOffset: 0L,
            srcSize: ((long)bitmap.Width << 32) | (uint)bitmap.Height,
            filterQuality: 1);
    }

    public string OwnerId { get; }
    public string TurnId { get; }
    public string PhotoUri { get; }

    public void FocusClose()
    {
        if (!_disposed)
            _closeFocus.RequestFocus();
    }

    public ComposableNode Render()
    {
        var screen = new Surface
        {
            Modifier = Modifier.FillMaxSize().TestTag("native.quiz.photo.viewer")
                .Semantics(semantics =>
                {
                    semantics.TestTagsAsResourceId(true);
                    semantics.ContentDescription(_title);
                }),
            Color = AndroidX.Compose.Color.Black,
            ContentColor = AndroidX.Compose.Color.White
        };
        var layers = new Box
        {
            Modifier = Modifier.FillMaxSize()
        };
        layers.Add(new Box
        {
            Modifier = Modifier.MatchParentSize()
                .TestTag("native.quiz.photo.backdrop")
                .DetectTapGestures(onTap: _ =>
                {
                    if (!_disposed)
                        _onDismiss();
                }, key: "photo-backdrop")
        });
        layers.Add(new BoxWithConstraints(layout =>
        {
            _ = _gestureRevision.Value;
            var density = _activity.Resources?.DisplayMetrics?.Density ?? 1f;
            if (!float.IsFinite(density) || density <= 0)
                density = 1f;
            _transform.SetViewport(layout.MaxWidth.Value * density,
                layout.MaxHeight.Value * density, _bitmap.Width, _bitmap.Height);
            var viewport = new Box
            {
                Modifier = Modifier.FillMaxSize().ClipToBounds()
                    .DetectTapGestures(
                        onTap: location =>
                        {
                            if (!_disposed && !_transform.ContainsImage(location.X, location.Y))
                                _onDismiss();
                        },
                        onDoubleTap: location =>
                        {
                            if (!_transform.ContainsImage(location.X, location.Y))
                            {
                                if (!_disposed)
                                    _onDismiss();
                            }
                            else
                                ChangeTransform(() =>
                                    _transform.DoubleTap(location.X, location.Y));
                        },
                        key: "photo-tap")
                    .DetectTransformGestures(
                        onGesture: (centroid, pan, zoom, _) => ChangeTransform(() =>
                            _transform.ZoomAndPan(centroid.X, centroid.Y,
                                pan.X, pan.Y, zoom)),
                        panZoomLock: true, key: "photo-transform"),
            };
            viewport.Add(new Image(_painter, _imageLabel)
            {
                Modifier = Modifier.Size(
                        Math.Max(1f, _transform.ImageWidth / density),
                        Math.Max(1f, _transform.ImageHeight / density))
                    .Align(Alignment.Center)
                    .GraphicsLayer(
                        scaleX: _transform.Scale, scaleY: _transform.Scale,
                        translationX: _transform.OffsetX,
                        translationY: _transform.OffsetY)
                    .TestTag("native.quiz.photo.image"),
                ContentScale = ContentScale.Fit
            });
            return viewport;
        })
        {
            Modifier = Modifier.FillMaxSize().SafeDrawingPadding().Padding(16)
        });
        var topBar = new Row(Arrangement.End)
        {
            Modifier = Modifier.FillMaxWidth().Align(Alignment.TopCenter)
                .SystemBarsPadding().Padding(16)
        };
        var close = new IconButton(onClick: () =>
        {
            if (!_disposed)
                _onDismiss();
        })
        {
            Modifier = Modifier.FocusRequester(_closeFocus).Size(48)
                .TestTag("native.quiz.photo.close")
                .Semantics(semantics => semantics.ContentDescription(_closeLabel))
        };
        close.Add(new Icon(Icons.Default.Close, _closeLabel)
        {
            Modifier = Modifier.Size(26),
            Tint = AndroidX.Compose.Color.White
        });
        topBar.Add(close);
        layers.Add(topBar);
        screen.Add(layers);
        return screen;
    }

    private void ChangeTransform(Action change)
    {
        if (_disposed)
            return;
        change();
        _gestureRevision.Value = unchecked(_gestureRevision.Value + 1);
    }

    public void Dispose() => _disposed = true;
}
