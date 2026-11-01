using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SwiftUI;
using SentenceStudio.Native.iOS.Styling;
using Views = Microsoft.SwiftUI.SwiftUI;

namespace SentenceStudio.Native.iOS.Features.Activities;

/// <summary>
/// SwiftUI How Do You Say (source HowDoYouSay.razor): the input card (label
/// "Enter a phrase in {language}", phrase field that speaks on Return, voice
/// menu when voices were found, full-width Speak button with a Generating
/// spinner), the source toast as an inline notice, and the History list of the
/// owner's spoken phrases with play/pause and delete. The field stores typed
/// text without re-rendering so Korean IME composition is not interrupted.
/// </summary>
internal sealed class HowDoYouSayScreen : IActivityScreen
{
    private readonly ActivityScreenHost _host;
    private readonly NativeHowDoYouSayPresenter _presenter;
    private NativeHowDoYouSayState _state = new(NativePhase.Loading);

    public HowDoYouSayScreen(ActivityScreenHost host)
    {
        _host = host;
        _presenter = host.Services.GetRequiredService<NativeHowDoYouSayPresenter>();
        _presenter.StateChanged += OnStateChanged;
    }

    public string TitleKey => NativeHowDoYouSayText.Title;

    public bool HasExited => _state.Destination == NativeDestination.TodaysPlan;

    /// <summary>
    /// Not awaited by the controller's busy action: the voice list can take the
    /// speech timeout, and Back must stay available meanwhile.
    /// </summary>
    public Task OpenAsync(NativeActivityLaunch launch, CancellationToken cancellationToken)
    {
        Fire(token => _presenter.OpenAsync(launch, token));
        return Task.CompletedTask;
    }

    public async Task ExitAsync(CancellationToken cancellationToken) =>
        _state = await _presenter.ExitAsync(cancellationToken);

    private void OnStateChanged(NativeHowDoYouSayState state)
    {
        _state = state;
        _host.Publish();
    }

    private void Fire(Func<CancellationToken, Task<NativeHowDoYouSayState>> action) =>
        _ = FireAsync(action);

    private async Task FireAsync(Func<CancellationToken, Task<NativeHowDoYouSayState>> action)
    {
        try
        {
            _state = await action(_host.Lifetime);
        }
        catch (OperationCanceledException) when (_host.Lifetime.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _host.Logger?.LogError(ex, "Native iOS How Do You Say action failed.");
            _state = _state with { ErrorKey = NativeText.UnexpectedError };
        }
        _host.Publish();
    }

    public SwiftView Render(SourceUiStyle style)
    {
        var text = _host.Text;
        var state = _state;
        var items = new List<SwiftView>();
        switch (state.Phase)
        {
            case NativePhase.Idle:
            case NativePhase.Loading:
                items.Add(Views.IndeterminateProgress(text(NativeHowDoYouSayText.Loading))
                    .WithAccessibilityIdentifier(NativeHowDoYouSayAccess.Loading));
                break;
            case NativePhase.Ready:
                if (state.ErrorKey is { } error)
                    items.Add(style.Alert(style.Text(Views.Text(text(error)), SourceTextRole.Body2),
                            success: false, compact: true)
                        .WithAccessibilityIdentifier(NativeHowDoYouSayAccess.Error));
                items.Add(InputCard(state, style, text));
                if (state.Notice is { } notice)
                    items.Add(style.Alert(style.Text(Views.Text(text(notice.Key)), SourceTextRole.Body2),
                            success: notice.Kind == NativeHowDoYouSayNoticeKind.Success, compact: true)
                        .WithAccessibilityIdentifier(NativeHowDoYouSayAccess.Notice));
                if (state.HistoryLoading)
                    items.Add(Views.IndeterminateProgress(text(NativeHowDoYouSayText.Loading))
                        .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center)
                        .WithAccessibilityIdentifier(NativeHowDoYouSayAccess.HistoryLoading));
                else if (state.ShowsHistory)
                {
                    items.Add(style.Text(Views.Text(text(NativeHowDoYouSayText.History)),
                            SourceTextRole.Title3, sourceHeading: true)
                        .WithAccessibilityIdentifier(NativeHowDoYouSayAccess.HistoryTitle));
                    items.Add(History(state, style, text));
                }
                break;
            default:
                items.Add(style.Text(Views.Text(text(state.ErrorKey ?? NativeHowDoYouSayText.Unavailable)),
                        SourceTextRole.Body1, SourceTextTone.Danger)
                    .WithAccessibilityIdentifier(NativeHowDoYouSayAccess.Error));
                items.Add(style.SecondaryButton(text(NativeHowDoYouSayText.GoBack), _host.Exit)
                    .WithAccessibilityIdentifier(NativeHowDoYouSayAccess.Back));
                break;
        }

        var gutter = SourceUiLayout.PageGutter(style.ViewportWidth);
        var content = Views.VStack(items.ToArray())
            .WithPadding(16, gutter, 16, gutter)
            .WithFlexibleWidth(alignment: state.Phase == NativePhase.Ready
                ? SwiftHorizontalAlignment.Leading : SwiftHorizontalAlignment.Center)
            .WithAccessibilityIdentifier("native.howdoyousay.content");
        content.Spacing = SourceUiLayout.PageGap;
        return style.Page(Views.Scroll(content)
            .WithAccessibilityIdentifier(NativeHowDoYouSayAccess.Screen));
    }

    private SwiftView InputCard(NativeHowDoYouSayState state, SourceUiStyle style,
        Func<string, string> text)
    {
        var language = NativeHowDoYouSayText.LanguageKey(state.TargetLanguage) is { } key
            ? text(key) : state.TargetLanguage;
        var label = string.Format(CultureInfo.CurrentCulture,
            text(NativeHowDoYouSayText.EnterPhrase), language);
        var rows = new List<SwiftView>();

        SwiftView speak;
        if (state.IsBusy)
        {
            var busy = Views.HStack(
                Views.IndeterminateProgress(text(NativeHowDoYouSayText.Generating)),
                Views.Text(text(NativeHowDoYouSayText.Generating)));
            busy.Spacing = 4;
            speak = style.LabelButton(busy, () => { }, SourceButtonRole.Primary)
                .WithFlexibleWidth()
                .WithAccessibilityIdentifier(NativeHowDoYouSayAccess.Generating);
            SourceUiStyle.SetCustomButtonAvailability(speak, style.Appearance,
                SourceButtonRole.Primary, enabled: false);
        }
        else
        {
            var speakLabel = Views.HStack(Views.Image("speaker.wave.2"),
                Views.Text(text(NativeHowDoYouSayText.Speak)));
            speakLabel.Spacing = 4;
            speakLabel.WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Center);
            speak = style.LabelButton(speakLabel, () => Fire(_presenter.SpeakAsync),
                    SourceButtonRole.Primary)
                .WithFlexibleWidth()
                .WithAccessibilityLabel(text(NativeHowDoYouSayText.Speak))
                .WithAccessibilityIdentifier(NativeHowDoYouSayAccess.Speak);
            SourceUiStyle.SetCustomButtonAvailability(speak, style.Appearance,
                SourceButtonRole.Primary, state.CanSpeak);
        }

        var field = style.TextInput(Views.TextField(text(NativeHowDoYouSayText.TypePhrase),
                state.Input, value =>
                {
                    // Store only: re-rendering here would replace the field mid-composition.
                    _presenter.SetInput(value);
                    if (!state.IsBusy)
                        SourceUiStyle.SetCustomButtonAvailability(speak, style.Appearance,
                            SourceButtonRole.Primary, !string.IsNullOrWhiteSpace(value));
                }))
            .WithFlexibleWidth()
            .WithAccessibilityLabel(label)
            .WithAccessibilityIdentifier(NativeHowDoYouSayAccess.Input)
            .OnSubmit(() => Fire(_presenter.SpeakAsync));
        var inputGroup = Views.VStack(
            style.Text(Views.Text(label), SourceTextRole.Body2, SourceTextTone.Secondary)
                .WithAccessibilityIdentifier(NativeHowDoYouSayAccess.InputLabel),
            field).WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Leading);
        inputGroup.Spacing = SourceUiLayout.ItemGap;
        rows.Add(inputGroup);

        if (state.ShowsVoices)
        {
            var voiceLabel = text(NativeHowDoYouSayText.Voice);
            var picker = style.Input(Views.Picker(string.Empty,
                        state.Voices.Select(voice => new SwiftPickerOption(voice.Id, voice.Label)).ToArray(),
                        state.SelectedVoiceId,
                        value => Fire(token => _presenter.SelectVoiceAsync(value, token)))
                    .WithPickerStyle(SwiftPickerStyle.Menu)
                    .WithAccessibilityLabel(voiceLabel)
                    .WithFlexibleWidth())
                .WithAccessibilityIdentifier(NativeHowDoYouSayAccess.Voice);
            var voiceGroup = Views.VStack(
                style.Text(Views.Text(voiceLabel), SourceTextRole.Body2, SourceTextTone.Secondary)
                    .WithAccessibilityIdentifier(NativeHowDoYouSayAccess.VoiceLabel),
                picker).WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Leading);
            voiceGroup.Spacing = SourceUiLayout.ItemGap;
            rows.Add(voiceGroup);
        }
        rows.Add(speak);

        var card = Views.VStack(rows.ToArray())
            .WithPadding(SourceUiLayout.CardInset)
            .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Leading);
        card.Spacing = SourceUiLayout.FormGap;
        return style.Card(card);
    }

    private SwiftView History(NativeHowDoYouSayState state, SourceUiStyle style,
        Func<string, string> text)
    {
        var appearance = style.Appearance;
        var rows = state.History.Select(entry =>
        {
            var playing = state.PlayingId == entry.Id;
            var details = Views.VStack(
                style.Text(Views.Text(entry.Phrase), SourceTextRole.Body1Bold, strongElement: true)
                    .WithAccessibilityIdentifier(NativeHowDoYouSayAccess.EntryPhrase(entry.Id)),
                style.Text(Views.Text(entry.CreatedAt.ToString("g", CultureInfo.CurrentCulture)),
                        SourceTextRole.Small, SourceTextTone.Secondary)
                    .WithAccessibilityIdentifier(NativeHowDoYouSayAccess.EntryTime(entry.Id)))
                .WithFlexibleWidth(alignment: SwiftHorizontalAlignment.Leading);
            details.Spacing = 2;

            var play = style.LabelButton(Views.Image(playing ? "pause.fill" : "play.fill"),
                    () => Fire(token => _presenter.TogglePlayAsync(entry.Id, token)),
                    playing ? SourceButtonRole.Primary : SourceButtonRole.FilledSecondary,
                    horizontalPadding: 12)
                .WithAccessibilityLabel(text(playing ? NativeHowDoYouSayText.Pause : NativeHowDoYouSayText.Play))
                .WithAccessibilitySelected(playing)
                .WithAccessibilityIdentifier(NativeHowDoYouSayAccess.EntryPlay(entry.Id));
            var delete = Views.Button(Views.Image("trash")
                        .WithForegroundColor(appearance.Danger.Light, appearance.Danger.Dark)
                        .WithPadding(10, 12, 10, 12)
                        .WithMinimumHeight(SourceUiLayout.MinimumTouchHeight),
                    () => Fire(token => _presenter.DeleteAsync(entry.Id, token)))
                .WithButtonStyle(SwiftButtonStyle.Plain)
                .WithButtonAppearance(null, null, appearance.Danger.Light, appearance.Danger.Dark,
                    appearance.BootstrapRadius)
                .WithAccessibilityLabel(text(NativeHowDoYouSayText.Delete))
                .WithAccessibilityIdentifier(NativeHowDoYouSayAccess.EntryDelete(entry.Id));
            var buttons = Views.HStack(play, delete);
            buttons.Spacing = 8;
            var row = Views.HStack(details, buttons)
                .WithPadding(SourceUiLayout.CardInset)
                .WithFlexibleWidth();
            row.Spacing = SourceUiLayout.ItemGap;
            return style.Card(row).WithAccessibilityIdentifier(NativeHowDoYouSayAccess.Entry(entry.Id));
        }).ToArray();
        // Lazy: the owner's audio history includes every activity's cached phrases.
        var list = Views.LazyVGrid(1, rows)
            .WithFlexibleWidth()
            .WithAccessibilityIdentifier(NativeHowDoYouSayAccess.History);
        list.Spacing = 8;
        return list;
    }

    public void Dispose()
    {
        _presenter.StateChanged -= OnStateChanged;
        _presenter.Dispose();
    }
}
