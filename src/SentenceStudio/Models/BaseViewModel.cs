
namespace SentenceStudio.Models;

public partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isConnected;

    public BaseViewModel()
    {
        Connectivity.ConnectivityChanged += OnConnectivityChanged;
        IsConnected = Connectivity.NetworkAccess == NetworkAccess.Internet;
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
