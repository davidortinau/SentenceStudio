using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SentenceStudio.Shared.Models;

public partial class Settings : ObservableObject
{
    [ObservableProperty]
    private string? openAIKey;

    [ObservableProperty]
    private string? elevenLabsKey;

    [ObservableProperty]
    private string? syncfusionKey;
}
