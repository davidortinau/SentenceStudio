
namespace SentenceStudio.Models;

public partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isBusy;

    public BaseViewModel()
    {
        Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
        IsConnected = Connectivity.Current.NetworkAccess == NetworkAccess.Internet;
    }

    private void OnConnectivityChanged(object sender, ConnectivityChangedEventArgs e)
    {
        IsConnected = e.NetworkAccess == NetworkAccess.Internet;
    }

    protected bool CanExecuteCommands()
    {
        return IsConnected;
    }
}
