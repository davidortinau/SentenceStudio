using SentenceStudio.Models;

namespace SentenceStudio.Pages.Warmup;

public class MessageTemplateSelector: DataTemplateSelector
{
    public DataTemplate MessageFromMe { get; set; }

    public DataTemplate MessageFromOtherTyping { get; set; }

    public DataTemplate MessageFromOthers { get; set; }

    public DataTemplate TopPaddingMessage { get; set; }

    public DataTemplate BottomPaddingMessage { get; set; }


    protected override DataTemplate OnSelectTemplate(object item, BindableObject container)
    {
        if (item is ConversationChunk conversationChunk)
        {
            if (conversationChunk.Author.Equals(ConversationParticipant.Bot.FirstName))
            {
                // if(string.IsNullOrWhiteSpace(conversationChunk.Text))
                //     return MessageFromOtherTyping;
                // else
                    return MessageFromOthers;
            }

            // if (conversationChunk is ConversationChunkPaddingTop)
            // {
            //     return TopPaddingMessage;
            // }

            // if (conversationChunk is ConversationChunkPaddingBottom)
            // {
            //     return BottomPaddingMessage;
            // }

            return MessageFromMe;
        }

        return null;
    }
}