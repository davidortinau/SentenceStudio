using SentenceStudio.Services.Speech;

namespace SentenceStudio.Api.Tests.Infrastructure;

/// <summary>
/// In-memory IVoiceDiscoveryService for SpeechEndpoints integration tests.
/// Records the language label the endpoint passed in, and returns a fixed
/// catalog of voices keyed by language label. Allows assertions on
/// (a) which label the endpoint resolved a given BCP-47 tag to, and
/// (b) the response shape (Gender/Accent trim + null-coalesce).
/// </summary>
public sealed class StubVoiceDiscoveryService : IVoiceDiscoveryService
{
    private readonly Dictionary<string, List<VoiceInfo>> _catalog =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Korean"] = new()
            {
                new VoiceInfo
                {
                    VoiceId = "ko-1",
                    Name = "Ji-Young",
                    Language = "Korean",
                    Gender = "Female",
                    Accent = "Seoul"
                },
                new VoiceInfo
                {
                    VoiceId = "ko-2",
                    Name = "Min-Jun",
                    Language = "Korean",
                    Gender = "  Male  ",
                    Accent = ""
                }
            },
            ["Spanish"] = new()
            {
                new VoiceInfo
                {
                    VoiceId = "es-1",
                    Name = "Lucia",
                    Language = "Spanish",
                    Gender = "Female",
                    Accent = "Castilian"
                }
            },
            ["English"] = new()
            {
                new VoiceInfo
                {
                    VoiceId = "en-1",
                    Name = "Sarah",
                    Language = "English",
                    Gender = "   ",
                    Accent = "American"
                }
            }
        };

    public List<string> RequestedLabels { get; } = new();

    public Task<List<VoiceInfo>> GetVoicesForLanguageAsync(string language, bool forceRefresh = false)
    {
        RequestedLabels.Add(language);
        if (_catalog.TryGetValue(language, out var voices))
        {
            return Task.FromResult(voices);
        }
        return Task.FromResult(new List<VoiceInfo>());
    }

    public string? GetLanguageCode(string language) => language switch
    {
        "Korean" => "ko",
        "Spanish" => "es",
        "English" => "en",
        _ => null
    };

    public IReadOnlyList<string> SupportedLanguages =>
        new[] { "Korean", "Spanish", "English" };

    public void ClearCache() { }
}
