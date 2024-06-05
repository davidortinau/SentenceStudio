using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using Sharpnado.Tasks;

namespace SentenceStudio;

public partial class AppShell : Shell
{
    private AppShellModel _model;

    public AppShell(AppShellModel model) 
    {
        InitializeComponent();
        BindingContext = _model = model;         
    }
	

	public static async Task DisplayToastAsync(string message)
    {
        ToastDuration duration = ToastDuration.Long;
        double fontSize = 14;
        var toast = Toast.Make(message, duration, fontSize);
        CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        await toast.Show(cancellationTokenSource.Token);
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        
        
    }

    void Shell_Navigating(object sender, ShellNavigatingEventArgs e)
    {
        Debug.WriteLine($"Navigating to {e.Target.Location}");
    }
}
