
namespace SentenceStudio.Messages
{
    using CommunityToolkit.Mvvm.Messaging.Messages;
    using SentenceStudio.Shared.Models;

    public class ConnectivityChangedMessage : ValueChangedMessage<bool>
    {
        public ConnectivityChangedMessage(bool isConnected) : base(isConnected)
        {
        }
    }
}