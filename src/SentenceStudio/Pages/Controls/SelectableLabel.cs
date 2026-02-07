using Microsoft.Maui.ApplicationModel.DataTransfer;
using UXDivers.Popups.Maui.Controls;
using UXDivers.Popups.Services;

namespace SentenceStudio.Pages.Controls;

partial class SelectableLabel : Component
{
	MauiControls.Label _labelRef;

	[Prop]
	string _text;

	[Prop]
	double _fontSize = 18.0;

	[Prop]
	Color _textColor;

	public override VisualNode Render()
	{
		var label = Label(labelRef => _labelRef = labelRef)
			.Text(_text)
			.FontSize(_fontSize);

		if (_textColor != null)
			label = label.TextColor(_textColor);

		return label.OnTapped(async () =>
			{
				if (_labelRef.Text != null)
				{
					await Clipboard.SetTextAsync(_labelRef.Text);

					var toast = new UXDivers.Popups.Maui.Controls.Toast { Title = "Copied to clipboard!" };
					await IPopupService.Current.PushAsync(toast);
					_ = Task.Delay(2500).ContinueWith(async _ =>
					{
						try { await IPopupService.Current.PopAsync(toast); } catch { }
					});
				}
			});
	}

}