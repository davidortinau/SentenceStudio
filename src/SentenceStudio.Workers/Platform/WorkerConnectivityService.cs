using System.Net.NetworkInformation;
using SentenceStudio.Abstractions;

namespace SentenceStudio.Workers.Platform;

public sealed class WorkerConnectivityService : IConnectivityService
{
    public bool IsInternetAvailable => NetworkInterface.GetIsNetworkAvailable();
}
