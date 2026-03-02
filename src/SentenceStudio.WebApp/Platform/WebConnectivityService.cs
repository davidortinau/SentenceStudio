using System.Net.NetworkInformation;
using SentenceStudio.Abstractions;

namespace SentenceStudio.WebApp.Platform;

public sealed class WebConnectivityService : IConnectivityService
{
    public bool IsInternetAvailable => NetworkInterface.GetIsNetworkAvailable();
}
