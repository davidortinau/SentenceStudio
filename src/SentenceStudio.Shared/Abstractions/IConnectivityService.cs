namespace SentenceStudio.Abstractions;

public interface IConnectivityService
{
    bool IsInternetAvailable { get; }
}
