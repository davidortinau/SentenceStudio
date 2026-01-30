namespace SentenceStudio.Services.LanguageSegmentation;

/// <summary>
/// German-specific text segmentation
/// </summary>
public class GermanLanguageSegmenter : GenericLatinSegmenter
{
    public override string LanguageCode => "de";
    public override string LanguageName => "German";

    protected override HashSet<string> TransitionWords => new(StringComparer.OrdinalIgnoreCase)
    {
        "jedoch", "deshalb", "außerdem", "darüber hinaus", "folglich",
        "dennoch", "trotzdem", "schließlich", "erstens", "zweitens",
        "zuletzt", "abschließend", "zum Beispiel", "im Gegensatz",
        "übrigens", "also", "dann", "danach", "anschließend",
        "denn", "weil", "obwohl", "obgleich", "sodass"
    };

    protected override HashSet<string> SentenceEndings => new(StringComparer.OrdinalIgnoreCase)
    {
        ".", "!", "?", "...", "!!", "?!"
    };

    protected override HashSet<string> TrivialPatterns => new(StringComparer.OrdinalIgnoreCase)
    {
        "hallo", "guten tag", "ja", "nein", "danke", "bitte",
        "entschuldigung", "auf wiedersehen", "tschüss", "guten morgen",
        "guten abend", "gute nacht", "okay", "genau", "stimmt"
    };

    protected override string[] FunctionWords => new[]
    {
        // Articles
        "der", "die", "das", "den", "dem", "des",
        "ein", "eine", "einen", "einem", "einer", "eines",
        // Prepositions
        "in", "an", "auf", "für", "mit", "von", "zu", "bei", "nach",
        "aus", "durch", "um", "über", "unter", "zwischen", "gegen",
        // Pronouns
        "ich", "du", "er", "sie", "es", "wir", "ihr",
        "mich", "dich", "sich", "uns", "euch",
        "mir", "dir", "ihm", "ihr",
        // Common verbs
        "ist", "sind", "hat", "haben", "wird", "werden",
        "war", "waren", "hatte", "hatten", "sein", "gewesen"
    };
}
