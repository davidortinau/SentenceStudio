namespace SentenceStudio.Services.LanguageSegmentation;

/// <summary>
/// Spanish-specific text segmentation
/// </summary>
public class SpanishLanguageSegmenter : GenericLatinSegmenter
{
    public override string LanguageCode => "es";
    public override string LanguageName => "Spanish";

    protected override HashSet<string> TransitionWords => new(StringComparer.OrdinalIgnoreCase)
    {
        "sin embargo", "por lo tanto", "además", "por consiguiente",
        "no obstante", "finalmente", "primero", "segundo",
        "en conclusión", "por ejemplo", "en cambio", "por otra parte",
        "así", "entonces", "luego", "después", "mientras tanto",
        "porque", "ya que", "aunque", "a pesar de"
    };

    protected override HashSet<string> SentenceEndings => new(StringComparer.OrdinalIgnoreCase)
    {
        ".", "!", "?", "...", "!!", "?!", "¡", "¿"
    };

    public override bool IsIncompleteSentence(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        var trimmedLine = line.Trim();

        // Spanish sentences can start with inverted punctuation
        // Check for standard endings
        if (trimmedLine.EndsWith(".") || trimmedLine.EndsWith("!") ||
            trimmedLine.EndsWith("?") || trimmedLine.EndsWith("..."))
            return false;

        return true;
    }

    protected override HashSet<string> TrivialPatterns => new(StringComparer.OrdinalIgnoreCase)
    {
        "hola", "sí", "no", "gracias", "por favor",
        "perdón", "adiós", "buenos días", "buenas tardes",
        "buenas noches", "de nada", "vale", "claro"
    };

    protected override string[] FunctionWords => new[]
    {
        // Articles
        "el", "la", "los", "las", "un", "una", "unos", "unas",
        // Prepositions
        "de", "del", "a", "al", "en", "con", "por", "para",
        "sin", "sobre", "entre", "hacia", "desde", "hasta",
        // Pronouns
        "yo", "tú", "él", "ella", "usted", "nosotros", "vosotros", "ellos", "ellas", "ustedes",
        "me", "te", "se", "nos", "os", "lo", "la", "le", "les",
        // Common verbs
        "es", "son", "está", "están", "ha", "han", "tiene", "tienen",
        "ser", "estar", "haber", "tener", "hacer"
    };
}
