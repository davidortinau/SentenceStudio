using System.Net.NetworkInformation;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Api.Platform;

public sealed class ApiConnectivityService : IConnectivityService
{
    public bool IsInternetAvailable => NetworkInterface.GetIsNetworkAvailable();
}
