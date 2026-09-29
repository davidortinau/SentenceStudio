using Microsoft.Maui.Networking;

namespace SentenceStudio.Abstractions;

public sealed class MauiConnectivityService : IConnectivityService
{
    public bool IsInternetAvailable => Connectivity.Current.NetworkAccess == NetworkAccess.Internet;
}
