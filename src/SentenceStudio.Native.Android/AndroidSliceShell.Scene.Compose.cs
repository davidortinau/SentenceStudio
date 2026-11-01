using System.Globalization;
using AndroidX.Compose;
using AndroidX.Compose.Runtime;
using AndroidX.Compose.UI.Graphics;
using AndroidX.Compose.UI.Graphics.Painter;
using AndroidX.Compose.UI.Text.Input;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;
using Bitmap = Android.Graphics.Bitmap;
using BitmapFactory = Android.Graphics.BitmapFactory;
using ComposeButton = AndroidX.Compose.Button;

namespace SentenceStudio.Native.Android;

/// <summary>
/// Compose Describe a Scene (source Scene.razor): gallery action in the top
/// app bar, the scene image, newest-first graded cards with More/Less, and the
/// source input bar (description, new scene, grade). The gallery overlay is a
/// Material 3 ModalBottomSheet; "Choose Photo" launches the system photo
/// picker (PickVisualMedia, Captain-approved). Fields bind Compose
/// TextFieldValue states, so typing (including Korean IME composition) never
/// goes through a presenter publish.
/// </summary>
internal sealed partial class AndroidSliceShell
{
    private const int SceneMaxPickedBytes = 25 * 1024 * 1024;
    private const int SceneMaxPhotoEdge = 2048;
    private const int SceneMaxBitmapEdge = 1280;
    private NativeScenePresenter? _scene;
    private readonly Dictionary<int, (byte[] Source, Bitmap? Bitmap)> _sceneBitmaps = [];

    internal NativeSceneState Scene { get; private set; } = new(NativePhase.Loading);

    internal MutableState<TextFieldValue> SceneDraft { get; } =
        new(ComposeExtensions.NewTextFieldValue(string.Empty));

    internal MutableState<TextFieldValue> SceneUrl { get; } =
        new(ComposeExtensions.NewTextFieldValue(string.Empty));

    private async Task OpenSceneAsync(NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        if (ActivityServices is not { } services)
            return;
        var presenter = services.GetRequiredService<NativeScenePresenter>();
        presenter.StateChanged += OnSceneChanged;
        _scene = presenter;
        Publish(() => Scene = new(NativePhase.Loading));
        var state = await presenter.OpenAsync(launch, cancellationToken);
        Publish(() =>
        {
            if (ReferenceEquals(_scene, presenter))
                ApplyScene(state);
        });
    }

    private async Task ExitSceneAsync(CancellationToken cancellationToken)
    {
        if (_scene is { } presenter)
            await presenter.ExitAsync(cancellationToken);
    }

    private void ReleaseScene()
    {
        if (_scene is { } presenter)
        {
            presenter.StateChanged -= OnSceneChanged;
            presenter.Dispose();
        }
        _scene = null;
        foreach (var entry in _sceneBitmaps.Values)
            entry.Bitmap?.Dispose();
        _sceneBitmaps.Clear();
        SceneDraft.Value = ComposeExtensions.NewTextFieldValue(string.Empty);
        SceneUrl.Value = ComposeExtensions.NewTextFieldValue(string.Empty);
    }

    private void OnSceneChanged(NativeSceneState state) =>
        Publish(() =>
        {
            if (_scene is not null)
                ApplyScene(state);
        });

    /// <summary>Only commands publish, so a differing draft or URL is one the presenter cleared.</summary>
    private void ApplyScene(NativeSceneState state)
    {
        Scene = state;
        if (!string.Equals(state.Draft, SceneDraft.Value.Text, StringComparison.Ordinal))
            SceneDraft.Value = ComposeExtensions.NewTextFieldValue(state.Draft);
        var url = state.Gallery?.NewImageUrl ?? string.Empty;
        if (!string.Equals(url, SceneUrl.Value.Text, StringComparison.Ordinal))
            SceneUrl.Value = ComposeExtensions.NewTextFieldValue(url);
    }

    /// <summary>Scene commands bypass the shell's busy gate so grading does not disable Back; the presenter serializes them.</summary>
    private void SceneCommand(Func<NativeScenePresenter, CancellationToken, Task<NativeSceneState>> command)
    {
        if (_scene is not { } presenter)
            return;
        presenter.SetDraft(SceneDraft.Value.Text);
        presenter.SetNewImageUrl(SceneUrl.Value.Text);
        _ = RunSceneAsync(presenter, command);
    }

    private async Task RunSceneAsync(NativeScenePresenter presenter,
        Func<NativeScenePresenter, CancellationToken, Task<NativeSceneState>> command)
    {
        try
        {
            var state = await command(presenter, _lifetime.Token);
            Publish(() =>
            {
                if (ReferenceEquals(_scene, presenter))
                    ApplyScene(state);
            });
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ShowFailure(ex);
        }
    }

    /// <summary>Launches the system photo picker; the chosen image is read, downscaled and saved off the UI thread.</summary>
    private void PickScenePhoto()
    {
        if (_scene is not { } presenter || Scene.Gallery is not { IsSaving: false })
            return;
        if (!activity.LaunchPhotoPicker(uri =>
            {
                if (uri is null || !ReferenceEquals(_scene, presenter))
                    return;
                var resolver = activity.ContentResolver;
                _ = Task.Run(async () =>
                {
                    var photo = resolver is null ? null : ReadScenePhoto(resolver, uri);
                    await RunSceneAsync(presenter, (scene, token) => photo is null
                        ? scene.PhotoPickFailedAsync("loadFailed", token)
                        : scene.AddPhotoAsync(photo, token));
                });
            }))
            SceneCommand((scene, token) => scene.PhotoPickFailedAsync("loadFailed", token));
    }

    /// <summary>Reads at most 25 MB (the iOS picker limit), applies no UI, and re-encodes as JPEG with a 2048-pixel long edge.</summary>
    private static NativeScenePickedPhoto? ReadScenePhoto(
        global::Android.Content.ContentResolver resolver, global::Android.Net.Uri uri)
    {
        try
        {
            using var input = resolver.OpenInputStream(uri);
            if (input is null)
                return null;
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = input.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (buffer.Length + read > SceneMaxPickedBytes)
                    return null;
                buffer.Write(chunk, 0, read);
            }
            var encoded = buffer.ToArray();
            using var bitmap = DecodeSampled(encoded, SceneMaxPhotoEdge);
            if (bitmap is null)
                return null;
            using var output = new MemoryStream();
            if (!bitmap.Compress(Bitmap.CompressFormat.Jpeg!, 85, output))
                return null;
            var bytes = output.ToArray();
            return bytes.Length is > 0 and <= NativeSceneImageStore.MaxImageBytes
                ? new NativeScenePickedPhoto(bytes, "image/jpeg")
                : null;
        }
        catch (Exception ex) when (ex is Java.Lang.Exception or IOException)
        {
            global::Android.Util.Log.Warn("SentenceStudio.Native",
                $"Scene photo could not be read ({ex.GetType().Name}).");
            return null;
        }
    }

    private static Bitmap? DecodeSampled(byte[] encoded, int maxEdge)
    {
        var options = new BitmapFactory.Options { InJustDecodeBounds = true };
        BitmapFactory.DecodeByteArray(encoded, 0, encoded.Length, options)?.Dispose();
        if (options.OutWidth <= 0 || options.OutHeight <= 0)
            return null;
        var sample = 1;
        while (Math.Max(options.OutWidth, options.OutHeight) / sample > maxEdge)
            sample *= 2;
        options.InJustDecodeBounds = false;
        options.InSampleSize = sample;
        return BitmapFactory.DecodeByteArray(encoded, 0, encoded.Length, options);
    }

    /// <summary>A display bitmap per image, decoded once per byte array.</summary>
    private Bitmap? SceneBitmap(int id, byte[] bytes)
    {
        if (_sceneBitmaps.TryGetValue(id, out var cached) && ReferenceEquals(cached.Source, bytes))
            return cached.Bitmap;
        cached.Bitmap?.Dispose();
        var bitmap = DecodeSampled(bytes, SceneMaxBitmapEdge);
        _sceneBitmaps[id] = (bytes, bitmap);
        return bitmap;
    }

    private ComposableNode? ScenePicture(int id, byte[] bytes, string label, int height)
    {
        if (SceneBitmap(id, bytes) is not { } bitmap)
            return null;
        var painter = BitmapPainterKt.BitmapPainter(
            image: AndroidImageBitmap_androidKt.AsImageBitmap(bitmap), srcOffset: 0L,
            srcSize: ((long)bitmap.Width << 32) | (uint)bitmap.Height, filterQuality: 1);
        return new Image(painter, label)
        {
            Modifier = Modifier.FillMaxWidth().Height(height),
            ContentScale = ContentScale.Fit
        };
    }

    private ComposableNode RenderScene(AndroidVisualPalette palette)
    {
        var state = Scene;
        var header = (TopAppBar)ScreenHeader(Localize(NativeSceneText.Title), palette,
            back: true, backAutomationId: NativeSceneAccess.Back);
        if (state.Phase is NativePhase.Ready or NativePhase.Empty
            && state.Destination == NativeDestination.Activity)
        {
            // Source PageHeader ToolbarActions: the image gallery icon.
            var label = Localize(NativeSceneText.ImageGallery);
            var gallery = new IconButton(() => SceneCommand((scene, token) => scene.OpenGalleryAsync(token)))
            {
                Modifier = Modifier.TestTag(NativeSceneAccess.OpenGallery)
                    .Semantics(semantics => semantics.ContentDescription(label)),
            };
            gallery.Add(new Icon(Icons.AutoMirrored.Default.List, label) { Modifier = Modifier.Size(24), Tint = palette.Text });
            header.Actions = gallery;
        }
        var viewport = new Column(Arrangement.SpacedBy(12))
        {
            Modifier = Modifier.FillMaxWidth().Weight(1f)
                .VerticalScroll(_quizScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 16)
                .TestTag(NativeSceneAccess.Screen),
        };
        if (!state.Notices.IsDefaultOrEmpty && state.Phase is NativePhase.Ready or NativePhase.Empty)
            viewport.Add(SceneNotices(state, palette));
        var ready = state.Phase == NativePhase.Ready && state.Image is not null
            && state.Destination == NativeDestination.Activity;
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                viewport.Add(new CircularProgressIndicator
                {
                    Modifier = Modifier.Size(28).TestTag(NativeSceneAccess.Loading)
                });
                viewport.Add(Body(Localize(NativeSceneText.Loading), palette, secondary: true));
                break;
            case NativePhase.Ready when ready:
                viewport.Add(SceneImage(state.Image!, palette));
                viewport.Add(Heading(Localize(NativeSceneText.Results), palette, 20,
                    Modifier.Semantics(semantics => semantics.Heading())));
                if (state.Results.IsDefaultOrEmpty)
                    viewport.Add(Body(Localize(NativeSceneText.SubmitDescription), palette, secondary: true,
                        modifier: Modifier.TestTag(NativeSceneAccess.ResultsEmpty)));
                else
                    foreach (var result in state.Results)
                        viewport.Add(SceneResult(result, state.ExpandedIndex == result.Index, palette));
                break;
            case NativePhase.Empty:
                viewport.Add(Body(Localize(NativeSceneText.NoSceneImages), palette, secondary: true,
                    modifier: Modifier.TestTag(NativeSceneAccess.Empty)));
                viewport.Add(Action(Localize(NativeSceneText.BrowseGallery), NativeSceneAccess.EmptyGallery,
                    () => SceneCommand((scene, token) => scene.OpenGalleryAsync(token)), true));
                viewport.Add(Action(Localize(NativeSceneText.GoBack), NativeSceneAccess.EmptyBack,
                    ExitActivity, !Busy.Value, primary: false));
                break;
            default:
                if (state.Destination == NativeDestination.Activity)
                {
                    viewport.Add(StatusAlert(Localize(state.ErrorKey ?? NativeSceneText.Unavailable), palette,
                        error: true, automationId: NativeSceneAccess.Error));
                    viewport.Add(Action(Localize(NativeSceneText.GoBack), NativeSceneAccess.EmptyBack,
                        ExitActivity, true, primary: false));
                }
                break;
        }

        var page = new Column
        {
            Modifier.FillMaxSize().TestTag("native.scene.page"),
            header,
            viewport,
        };
        if (ready)
            page.Add(SceneInputBar(state, palette));
        if (state.Gallery is { } open && state.Destination == NativeDestination.Activity)
            page.Add(SceneGallerySheet(open, state, palette));
        return page;
    }

    private ComposableNode SceneNotices(NativeSceneState state, AndroidVisualPalette palette)
    {
        var list = new Column(Arrangement.SpacedBy(6))
        {
            Modifier.Weight(1f).TestTag(NativeSceneAccess.Notices),
        };
        var index = 0;
        foreach (var notice in state.Notices)
        {
            var message = notice.Argument is null ? Localize(notice.Key)
                : string.Format(CultureInfo.CurrentCulture, Localize(notice.Key), notice.Argument);
            list.Add(StatusAlert(message, palette,
                error: notice.Kind == NativeSceneNoticeKind.Error,
                success: notice.Kind == NativeSceneNoticeKind.Success,
                automationId: $"{NativeSceneAccess.Notice}.{index++}"));
        }
        var dismissLabel = Localize(NativeSceneText.Dismiss);
        var dismiss = new IconButton(
            () => SceneCommand((scene, token) => scene.DismissNoticesAsync(token)))
        {
            Modifier = Modifier.TestTag(NativeSceneAccess.DismissNotices)
                .Semantics(semantics => semantics.ContentDescription(dismissLabel)),
        };
        dismiss.Add(new Icon(Icons.Default.Close, dismissLabel) { Modifier = Modifier.Size(20), Tint = palette.Text });
        return new Row(Arrangement.SpacedBy(8))
        {
            Modifier.FillMaxWidth(),
            list,
            dismiss,
        };
    }

    /// <summary>Source scene image card (max-height 400, object-fit contain).</summary>
    private ComposableNode SceneImage(NativeSceneImage image, AndroidVisualPalette palette)
    {
        var label = Localize(NativeSceneText.SceneImage);
        var picture = image.Load == NativeSceneImageLoad.Loaded && image.Bytes is { } bytes
            ? ScenePicture(image.Id, bytes, label, 300) : null;
        var tag = picture is not null ? NativeSceneAccess.Image
            : image.Load == NativeSceneImageLoad.Loading ? NativeSceneAccess.ImageLoading
            : NativeSceneAccess.ImageFailed;
        var surface = new Surface
        {
            Modifier = Modifier.FillMaxWidth().HeightIn(min: 160).TestTag(tag)
                .Semantics(semantics => semantics.ContentDescription(label)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.CardRadius),
            Color = palette.Card,
        };
        if (picture is not null)
            surface.Add(picture);
        else if (image.Load == NativeSceneImageLoad.Loading)
            surface.Add(new CircularProgressIndicator { Modifier = Modifier.Size(28).Padding(8) });
        else
            surface.Add(Body(Localize(NativeSceneText.ImageFailed), palette, secondary: true,
                modifier: Modifier.Padding(16)));
        return surface;
    }

    /// <summary>Source result card: answer, colored accuracy badge, explanation, suggestion, More/Less details.</summary>
    private ComposableNode SceneResult(NativeSceneResult result, bool expanded, AndroidVisualPalette palette)
    {
        var id = result.AccessibilityId;
        var color = result.Accuracy >= 80 ? palette.Success
            : result.Accuracy >= 50 ? palette.WarningText : palette.Danger;
        var percent = $"{(int)result.Accuracy}%";
        var accuracyLabel = $"{Localize("Common_Accuracy")} {percent}";
        var children = new List<ComposableNode>
        {
            new Row(Arrangement.SpacedBy(12), Alignment.Vertical.CenterVertically)
            {
                Modifier.FillMaxWidth(),
                new Text(result.Answer)
                {
                    Modifier = Modifier.Weight(1f).TestTag($"{id}.answer"),
                    Color = palette.Text,
                    FontSize = new Sp(16),
                    FontWeight = FontWeight.SemiBold,
                },
                new Text(percent)
                {
                    Modifier = Modifier.TestTag($"{id}.accuracy")
                        .Semantics(semantics => semantics.ContentDescription(accuracyLabel)),
                    Color = color,
                    FontSize = new Sp(15),
                    FontWeight = FontWeight.SemiBold,
                },
            },
        };
        if (!string.IsNullOrEmpty(result.AccuracyExplanation))
            children.Add(Body(result.AccuracyExplanation, palette, secondary: true,
                modifier: Modifier.TestTag($"{id}.explanation")));
        if (!string.IsNullOrEmpty(result.RecommendedSentence))
            children.Add(new Text(result.RecommendedSentence)
            {
                Modifier = Modifier.TestTag($"{id}.suggested"),
                Color = palette.Success,
                FontSize = new Sp(14),
            });
        if (expanded)
        {
            var details = new Column(Arrangement.SpacedBy(4))
            {
                Modifier.FillMaxWidth().TestTag($"{id}.details"),
            };
            if (result.Fluency > 0)
                details.Add(Body($"{Localize(NativeSceneText.Fluency)}: {(int)result.Fluency}%", palette, secondary: true));
            if (!string.IsNullOrEmpty(result.FluencyExplanation))
                details.Add(Body(result.FluencyExplanation, palette, secondary: true));
            if (!string.IsNullOrEmpty(result.GrammarNotes))
                details.Add(Body(result.GrammarNotes, palette, secondary: true));
            children.Add(details);
        }
        var toggleLabel = Localize(expanded ? NativeSceneText.Less : NativeSceneText.More);
        children.Add(new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
        {
            Modifier.TestTag($"{id}.toggle")
                .Clickable(() => SceneCommand((scene, token) => scene.ToggleDetailsAsync(result.Index, token)))
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(toggleLabel);
                    semantics.Role(SemanticsRole.Button);
                    semantics.Selected(expanded);
                }),
            Body(toggleLabel, palette, secondary: true),
            new Icon(expanded ? Icons.Default.KeyboardArrowUp : Icons.Default.KeyboardArrowDown, null)
            {
                Modifier = Modifier.Size(18),
                Tint = palette.SecondaryText,
            },
        });
        return Card(palette, id, [.. children]);
    }

    private ComposableNode SceneInputBar(NativeSceneState state, AndroidVisualPalette palette)
    {
        var hasText = !string.IsNullOrWhiteSpace(SceneDraft.Value.Text);
        var placeholder = Localize(NativeSceneText.DescribeWhatYouSee);
        var keyboard = KeyboardOptionsCompanion.Default;
        var field = new OutlinedTextField(SceneDraft, enabled: true, singleLine: true)
        {
            Modifier = Modifier.Weight(1f).TestTag(NativeSceneAccess.Input)
                .Semantics(semantics => semantics.ContentDescription(placeholder)),
            Label = new Text(placeholder),
            KeyboardOptions = keyboard.Copy(
                keyboard.Capitalization, keyboard.AutoCorrectEnabled,
                AndroidX.Compose.KeyboardType.Text,
                AndroidX.Compose.ImeAction.Done,
                keyboard.PlatformImeOptions, keyboard.ShowKeyboardOnFocus,
                keyboard.HintLocales),
            KeyboardActions = KeyboardActionsHelper.Create(onDone: () =>
            {
                if (!string.IsNullOrWhiteSpace(SceneDraft.Value.Text))
                    SceneCommand((scene, token) => scene.GradeAsync(token));
            }),
        };
        var newLabel = Localize(NativeSceneText.Loading);
        var newScene = new IconButton(
            () => SceneCommand((scene, token) => scene.NewSceneAsync(token)),
            enabled: state.CanChangeScene)
        {
            Modifier = Modifier.TestTag(NativeSceneAccess.NewScene)
                .Semantics(semantics => semantics.ContentDescription(newLabel)),
        };
        newScene.Add(new Icon(Icons.Default.Refresh, newLabel) { Modifier = Modifier.Size(24), Tint = palette.Text });
        var gradeLabel = Localize(NativeSceneText.Grade);
        var grade = new IconButton(
            () => SceneCommand((scene, token) => scene.GradeAsync(token)),
            enabled: hasText && !state.IsGrading)
        {
            Modifier = Modifier.TestTag(NativeSceneAccess.Grade)
                .Semantics(semantics => semantics.ContentDescription(gradeLabel)),
        };
        if (state.IsGrading)
            grade.Add(new CircularProgressIndicator { Modifier = Modifier.Size(20) });
        else
            grade.Add(new Icon(Icons.AutoMirrored.Default.Send, gradeLabel)
            {
                Modifier = Modifier.Size(24),
                Tint = hasText ? palette.Primary : palette.SecondaryText,
            });
        return new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth()
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: 8)
                .TestTag("native.scene.input-bar"),
            field,
            newScene,
            grade,
        };
    }

    /// <summary>Source scene-gallery-overlay as a ModalBottomSheet.</summary>
    private ComposableNode SceneGallerySheet(NativeSceneGallery gallery, NativeSceneState state,
        AndroidVisualPalette palette)
    {
        var sheet = new ModalBottomSheet(
            onDismissRequest: () => SceneCommand((scene, token) => scene.CloseGalleryAsync(token)))
        {
            Modifier = Modifier.TestTag(NativeSceneAccess.Gallery),
        };
        var content = new Column(Arrangement.SpacedBy(12))
        {
            Modifier.FillMaxWidth().VerticalScroll(_quizScroll)
                .Padding(horizontal: AndroidVisualTheme.PagePadding, vertical: AndroidVisualTheme.CardPadding),
        };
        var header = new Row(Arrangement.SpacedBy(4), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            Heading(Localize(NativeSceneText.ImageGallery), palette, 22,
                Modifier.Weight(1f).Semantics(semantics => semantics.Heading())),
        };
        if (gallery.IsSelecting)
        {
            var deleteLabel = string.Format(CultureInfo.CurrentCulture, Localize(NativeSceneText.Delete),
                gallery.SelectedCount);
            var delete = new ComposeButton(
                onClick: () => SceneCommand((scene, token) => scene.DeleteSelectedAsync(token)),
                enabled: gallery.CanDelete)
            {
                Modifier = Modifier.TestTag(NativeSceneAccess.GalleryDelete)
                    .Semantics(semantics => semantics.ContentDescription(deleteLabel)),
                Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            };
            delete.Add(new Icon(Icons.Default.Delete, null) { Modifier = Modifier.Size(18) });
            delete.Add(new Text(deleteLabel));
            header.Add(delete);
        }
        var selectLabel = Localize(NativeSceneText.SelectMultiple);
        var select = new IconButton(() => SceneCommand((scene, token) => scene.ToggleSelectModeAsync(token)))
        {
            Modifier = Modifier.TestTag(NativeSceneAccess.GallerySelect)
                .Semantics(semantics =>
                {
                    semantics.ContentDescription(selectLabel);
                    semantics.Selected(gallery.IsSelecting);
                }),
        };
        select.Add(new Icon(gallery.IsSelecting ? Icons.Default.CheckCircle : Icons.Default.Check, selectLabel)
        {
            Modifier = Modifier.Size(24),
            Tint = gallery.IsSelecting ? palette.Primary : palette.Text,
        });
        header.Add(select);
        var closeLabel = Localize(NativeSceneText.Cancel);
        var close = new IconButton(() => SceneCommand((scene, token) => scene.CloseGalleryAsync(token)))
        {
            Modifier = Modifier.TestTag(NativeSceneAccess.GalleryClose)
                .Semantics(semantics => semantics.ContentDescription(closeLabel)),
        };
        close.Add(new Icon(Icons.Default.Close, closeLabel) { Modifier = Modifier.Size(24), Tint = palette.Text });
        header.Add(close);
        content.Add(header);

        var urlLabel = Localize(NativeSceneText.PasteImageUrl);
        var keyboard = KeyboardOptionsCompanion.Default;
        var url = new OutlinedTextField(SceneUrl, enabled: true, singleLine: true)
        {
            Modifier = Modifier.Weight(1f).TestTag(NativeSceneAccess.GalleryUrl)
                .Semantics(semantics => semantics.ContentDescription(urlLabel)),
            Label = new Text(urlLabel),
            KeyboardOptions = keyboard.Copy(
                keyboard.Capitalization, keyboard.AutoCorrectEnabled,
                AndroidX.Compose.KeyboardType.Text,
                AndroidX.Compose.ImeAction.Done,
                keyboard.PlatformImeOptions, keyboard.ShowKeyboardOnFocus,
                keyboard.HintLocales),
            KeyboardActions = KeyboardActionsHelper.Create(onDone: () =>
                SceneCommand((scene, token) => scene.AddImageUrlAsync(token))),
        };
        var addLabel = Localize(NativeSceneText.AddImage);
        var add = new ComposeButton(
            onClick: () => SceneCommand((scene, token) => scene.AddImageUrlAsync(token)),
            enabled: !gallery.IsSaving && !string.IsNullOrWhiteSpace(SceneUrl.Value.Text))
        {
            Modifier = Modifier.TestTag(NativeSceneAccess.GalleryAdd)
                .Semantics(semantics => semantics.ContentDescription(addLabel)),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
        };
        add.Add(new Icon(Icons.Default.Add, null) { Modifier = Modifier.Size(18) });
        add.Add(new Text(addLabel));
        content.Add(new Row(Arrangement.SpacedBy(8), Alignment.Vertical.CenterVertically)
        {
            Modifier.FillMaxWidth(),
            url,
            add,
        });
        content.Add(Action(Localize(NativeSceneText.ChoosePhoto), NativeSceneAccess.GalleryPickPhoto,
            PickScenePhoto, !gallery.IsSaving, primary: false));
        if (!state.Notices.IsDefaultOrEmpty)
            content.Add(SceneNotices(state, palette));

        if (gallery.IsLoading)
            content.Add(new CircularProgressIndicator
            {
                Modifier = Modifier.Size(28).TestTag(NativeSceneAccess.GalleryLoading)
            });
        else if (gallery.Items.IsDefaultOrEmpty)
            content.Add(Body(Localize(NativeSceneText.GalleryEmpty), palette, secondary: true,
                modifier: Modifier.TestTag(NativeSceneAccess.GalleryEmpty)));
        else
        {
            const int columns = 3;
            var items = gallery.Items;
            for (var start = 0; start < items.Length; start += columns)
            {
                var row = new Row(Arrangement.SpacedBy(8))
                {
                    Modifier.FillMaxWidth(),
                };
                foreach (var item in items.Skip(start).Take(columns))
                    row.Add(SceneGalleryTile(item, gallery.IsSelecting, state.IsGrading, palette));
                content.Add(row);
            }
        }
        sheet.Add(content);
        return sheet;
    }

    private ComposableNode SceneGalleryTile(NativeSceneGalleryItem item, bool selecting, bool grading,
        AndroidVisualPalette palette)
    {
        var sceneLabel = Localize(NativeSceneText.SceneImage);
        var label = item.IsCurrent
            ? $"{sceneLabel} {item.Id}, {Localize(NativeSceneText.Current)}"
            : $"{sceneLabel} {item.Id}";
        var enabled = selecting || !grading;
        var modifier = Modifier.Weight(1f).TestTag(item.AccessibilityId)
            .Semantics(semantics =>
            {
                semantics.ContentDescription(label);
                semantics.Role(SemanticsRole.Button);
                semantics.Selected(selecting ? item.IsChecked : item.IsCurrent);
            });
        if (enabled)
            modifier = modifier.Clickable(() =>
                SceneCommand((scene, token) => scene.TapGalleryItemAsync(item.Id, token)));
        var tile = new Surface
        {
            Modifier = modifier.Border(item.IsChecked || item.IsCurrent ? 2 : 1,
                item.IsChecked || item.IsCurrent ? palette.Primary : palette.Border,
                AndroidVisualTheme.ControlRadius),
            Shape = Shape.RoundedCorners(AndroidVisualTheme.ControlRadius),
            Color = palette.Card,
        };
        var column = new Column(Arrangement.SpacedBy(4))
        {
            Modifier.FillMaxWidth().Padding(4),
        };
        var picture = item.Load == NativeSceneImageLoad.Loaded && item.Bytes is { } bytes
            ? ScenePicture(item.Id, bytes, sceneLabel, 96) : null;
        if (picture is not null)
            column.Add(picture);
        else if (item.Load == NativeSceneImageLoad.Loading)
            column.Add(new CircularProgressIndicator { Modifier = Modifier.Size(24) });
        else
            column.Add(Body(Localize(NativeSceneText.ImageFailed), palette, secondary: true));
        if (item.IsCurrent)
            column.Add(new Text(Localize(NativeSceneText.Current))
            {
                Modifier = Modifier.TestTag($"{item.AccessibilityId}.current"),
                Color = palette.Primary,
                FontSize = new Sp(12),
                FontWeight = FontWeight.SemiBold,
            });
        if (selecting)
            column.Add(new Icon(item.IsChecked ? Icons.Default.CheckCircle : Icons.Default.Check, null)
            {
                Modifier = Modifier.Size(20).TestTag($"{item.AccessibilityId}.checked"),
                Tint = item.IsChecked ? palette.Primary : palette.SecondaryText,
            });
        tile.Add(column);
        return tile;
    }
}
