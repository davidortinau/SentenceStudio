using SentenceStudio.Abstractions;

namespace SentenceStudio.Services;

public interface IClientAuthStateNotifier
{
    void NotifySignedIn();

    Task<SignOutOutcome> LogOutAsync();
}
