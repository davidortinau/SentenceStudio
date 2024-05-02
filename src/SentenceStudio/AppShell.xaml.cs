using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;

namespace SentenceStudio;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();
		BindingContext = new AppShellModel();
	}

	public static async Task DisplayToastAsync(string message)
    {
        ToastDuration duration = ToastDuration.Long;
        double fontSize = 14;
        var toast = Toast.Make(message, duration, fontSize);
        CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        await toast.Show(cancellationTokenSource.Token);
    }
}
