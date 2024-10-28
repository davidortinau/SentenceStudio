

namespace SentenceStudio.Models;

public partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isBusy;
    private bool _isNavigatedTo;
    private bool _dataLoaded;

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

    [RelayCommand]
    private void NavigatedTo() =>
        _isNavigatedTo = true;

    [RelayCommand]
    private void NavigatedFrom() =>
        _isNavigatedTo = false;

    [RelayCommand]
    private async Task Appearing()
    {
        if (!_dataLoaded)
        {
            await InitData();
            _dataLoaded = true;
            await Refresh();
        }
        // This means we are being navigated to
        else if (!_isNavigatedTo)
        {
            await Refresh();
        }
    }

    public virtual async Task Refresh()
    {
    }

    public virtual async Task InitData()
    {
    }
}
