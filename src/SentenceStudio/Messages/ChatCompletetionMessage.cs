
namespace SentenceStudio.Messages
{
    using CommunityToolkit.Mvvm.Messaging.Messages;
    using SentenceStudio.Shared.Models;

    public class ChatCompletionMessage : ValueChangedMessage<string>
    {
        public ChatCompletionMessage(string message) : base(message)
        {
        }
    }
}