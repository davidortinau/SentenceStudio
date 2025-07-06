namespace SentenceStudio.Shared.Models;

public class ConversationParticipant
{
    public static readonly ConversationParticipant Me = new ConversationParticipant(
        "David",
        "Ortinau",
        "https://avatars.githubusercontent.com/u/41873?v=4",
        "Hi!");

    public static readonly ConversationParticipant Bot = new ConversationParticipant(
        "김철수",
        "",
        "https://randomuser.me/api/portraits/men/37.jpg",
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
