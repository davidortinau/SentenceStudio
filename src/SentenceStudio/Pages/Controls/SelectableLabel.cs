using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;

namespace SentenceStudio.Pages.Controls;

partial class SelectableLabel : Component
{
	MauiControls.Label _labelRef;

	[Prop]
	string _text;

	public override VisualNode Render() 
		=> Label(labelRef => _labelRef = labelRef)
			.Text(_text)
			.ThemeKey(ApplicationTheme.Body1)
			.TextColor(Colors.White)
			.OnTapped(async () =>
			{
				if (_labelRef.Text != null)
				{
					await Clipboard.SetTextAsync(_labelRef.Text);

					CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
					string text = "Copied to clipboard!";
					ToastDuration duration = ToastDuration.Short;
					double fontSize = 14;
					var toast = Toast.Make(text, duration, fontSize);
					await toast.Show(cancellationTokenSource.Token);
				}
			});
	
}