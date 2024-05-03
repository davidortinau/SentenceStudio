using CommunityToolkit.Maui.Alerts;
using CommunityToolkit.Maui.Core;
using Sharpnado.Tasks;

namespace SentenceStudio;

public partial class AppShell : Shell
{
    private AppShellModel _model;

    public AppShell()
    {
        InitializeComponent();
        
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

    protected override void OnHandlerChanged()
    {
        base.OnHandlerChanged();
        BindingContext = _model = this.Handler.MauiContext.Services.GetService<AppShellModel>();
        TaskMonitor.Create(_model.LoadProfile);
    }
}
