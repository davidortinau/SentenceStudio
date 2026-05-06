namespace SentenceStudio.Services.Numbers;

public interface INumberItemGenerator
{
    string LanguageCode { get; }
    NumberItem GenerateItem(NumberItemRequest request);
}
