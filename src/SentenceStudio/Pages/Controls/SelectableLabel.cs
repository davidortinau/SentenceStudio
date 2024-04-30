using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;

namespace SentenceStudio.Pages.Controls;

public class SelectableLabel : Label
{
	public SelectableLabel()
	{
		var tapGestureRecognizer = new TapGestureRecognizer();
		tapGestureRecognizer.NumberOfTapsRequired = 1;
		tapGestureRecognizer.Tapped += async (s, e) =>
		{
			if (Text != null)
			{
				await Clipboard.SetTextAsync(Text);

				CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
				string text = "Copied to clipboard!";
				ToastDuration duration = ToastDuration.Short;
				double fontSize = 14;
				var toast = Toast.Make(text, duration, fontSize);
				await toast.Show(cancellationTokenSource.Token);
			}
		};
		this.GestureRecognizers.Add(tapGestureRecognizer);
	}
}