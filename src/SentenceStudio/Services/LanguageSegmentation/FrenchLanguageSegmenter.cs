namespace SentenceStudio.Services.LanguageSegmentation;

/// <summary>
/// French-specific text segmentation
/// </summary>
public class FrenchLanguageSegmenter : GenericLatinSegmenter
{
    public override string LanguageCode => "fr";
    public override string LanguageName => "French";

    protected override HashSet<string> TransitionWords => new(StringComparer.OrdinalIgnoreCase)
    {
        "cependant", "donc", "de plus", "en outre", "par conséquent",
        "néanmoins", "toutefois", "enfin", "premièrement", "deuxièmement",
        "finalement", "en conclusion", "par exemple", "en revanche",
        "d'ailleurs", "ainsi", "alors", "ensuite", "puis", "pourtant",
        "car", "parce que", "puisque", "bien que", "même si"
    };

    protected override HashSet<string> SentenceEndings => new(StringComparer.OrdinalIgnoreCase)
    {
        ".", "!", "?", "...", "!!", "?!", "»", "»."
    };

    protected override HashSet<string> TrivialPatterns => new(StringComparer.OrdinalIgnoreCase)
    {
        "bonjour", "salut", "oui", "non", "merci", "pardon",
        "s'il vous plaît", "au revoir", "bonsoir", "d'accord"
    };

    protected override string[] FunctionWords => new[]
    {
        // Articles
        "le", "la", "les", "l'", "un", "une", "des",
        // Prepositions
        "de", "du", "à", "au", "aux", "en", "dans", "sur", "sous",
        "pour", "par", "avec", "sans", "chez", "vers", "entre",
        // Pronouns
        "je", "tu", "il", "elle", "on", "nous", "vous", "ils", "elles",
        "me", "te", "se", "lui", "leur", "y", "en",
        // Common verbs
        "est", "sont", "a", "ont", "fait", "été", "avoir", "être"
    };
}
