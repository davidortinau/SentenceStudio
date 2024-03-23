namespace SentenceStudio.Models;

public class ConversationParticipant
{
    public static readonly ConversationParticipant Me = new ConversationParticipant(
        "Xam",
        "Xappy",
        "https://pbs.twimg.com/profile_images/1118743728003452928/oMJdZl-C_400x400.png",
        "안녕하세요!");

    public ConversationParticipant(string firstName, string lastName, string avatarUrl, string spamSentence)
    {
        FirstName = firstName;
        LastName = lastName;
        AvatarUrl = avatarUrl;
        SpamSentence = spamSentence;
    }

    public string FirstName { get; }

    public string LastName { get; }

    public string AvatarUrl { get; }

    public string SpamSentence { get; }
}