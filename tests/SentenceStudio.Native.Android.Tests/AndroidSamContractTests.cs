using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SentenceStudio.Contracts.Coach;

namespace SentenceStudio.Native.Android.Tests;

/// <summary>
/// Source contracts for the Compose Sam head: Compose-only presentation, the
/// shared presenter (no forked business logic), iOS-matching native.sam ids,
/// en/ko resources for every key it resolves, and owner-boundary clearing.
/// </summary>
public sealed class AndroidSamContractTests
{
    private static readonly string[] SamFiles =
    [
        "AndroidSliceShell.Sam.cs",
        "AndroidSliceShell.Sam.Compose.cs",
        "AndroidSamProjection.cs",
    ];

    [Fact]
    public void SendPushesTheLatestTypedTextAndAccountChangeClearsTheDraft()
    {
        var shell = File.ReadAllText(Path.Combine(Root, "AndroidSliceShell.Sam.cs"));
        var send = Between(shell, "private void SendSam()", "\n    }");
        Assert.True(send.IndexOf("_sam?.SetDraft(typed)", StringComparison.Ordinal)
            < send.IndexOf("SamState.CanSend", StringComparison.Ordinal));
        var clear = Between(shell, "private void ClearSamForAccountBoundary()", "\n    }");
        Assert.Contains("_samDraftSynced = string.Empty;", clear);
        Assert.Contains("NewTextFieldValue(string.Empty)", clear);
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, start);
        var to = text.IndexOf(end, from, StringComparison.Ordinal);
        return text[from..to];
    }

    [Fact]
    public void SamIsComposeOnlyAndBindsTheSharedPresenter()
    {
        foreach (var file in SamFiles)
        {
            var source = Code(file);
            Assert.DoesNotMatch(@"Android\.Widget|AndroidX\.AppCompat|\bAndroidView\b|Android\.App\.Dialog|Microsoft\.Maui", source);
        }
        var compose = Code("AndroidSliceShell.Sam.Compose.cs");
        Assert.Contains("new ModalBottomSheet(onDismissRequest: CloseSam", compose);
        Assert.Contains("new LazyColumn<AndroidSamRow>", compose);
        Assert.Contains("new ExtendedFloatingActionButton(OpenSam", compose);
        Assert.Contains("new BackHandler(ToggleSamConversations", compose);
        var wiring = Code("AndroidSliceShell.Sam.cs");
        Assert.Contains("GetRequiredService<NativeSamPresenter>()", wiring);
        Assert.DoesNotContain("ICoachApiClient", wiring + compose);
        Assert.Contains("services.AddOwnerBoundNativeSam(apiUri);", Code("AndroidServiceHost.cs"));
    }

    [Theory]
    [InlineData("native.sam.fab")]
    [InlineData("native.sam.panel")]
    [InlineData("native.sam.stream")]
    [InlineData("native.sam.composer")]
    [InlineData("native.sam.send")]
    [InlineData("native.sam.counter")]
    [InlineData("native.sam.close")]
    [InlineData("native.sam.size")]
    [InlineData("native.sam.conversations.toggle")]
    [InlineData("native.sam.conversations.new")]
    [InlineData("native.sam.conversation.")]
    [InlineData("native.sam.new")]
    [InlineData("native.sam.suggestion.accept")]
    [InlineData("native.sam.suggestion.reject")]
    [InlineData("native.sam.receipt.undo")]
    [InlineData("native.sam.vocabset.approve")]
    [InlineData("native.sam.vocabset.decline")]
    [InlineData("native.sam.vocabset.start")]
    [InlineData("native.sam.chip.")]
    [InlineData("native.sam.status")]
    [InlineData("native.sam.retry")]
    [InlineData("native.sam.stop")]
    [InlineData("native.sam.reloadplan")]
    [InlineData("native.sam.keepplan")]
    [InlineData("native.sam.startnew")]
    public void EverySamSurfaceHasTheIosTestTag(string id) =>
        Assert.Contains($"\"{id}", string.Concat(SamFiles.Select(Code)));

    [Theory]
    [InlineData("AppResources.resx")]
    [InlineData("AppResources.ko.resx")]
    public void EveryResourceKeyTheComposeSamResolvesExistsInEnglishAndKorean(string file)
    {
        var names = XDocument.Load(Path.Combine(Root, "..", "SentenceStudio.Shared", "Resources",
                "Strings", file)).Descendants("data")
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Element("value")?.Value))
            .Select(entry => entry.Attribute("name")!.Value)
            .ToHashSet(StringComparer.Ordinal);
        var literal = Regex.Matches(string.Concat(SamFiles.Select(Code)),
                "\"((?:Coach|NativeSam|Common)_[A-Za-z_]+)\"")
            .Select(match => match.Groups[1].Value)
            .Where(key => !key.EndsWith('_'))
            .Distinct()
            .ToArray();
        Assert.NotEmpty(literal);
        var composed = Enum.GetNames<CoachAnswerBlockKind>().Where(kind => kind != "Answer")
            .Select(kind => "Coach_AnswerBlock_" + kind);
        Assert.Empty(literal.Concat(composed).Where(key => !names.Contains(key)));
    }

    /// <summary>Every owner change and sign-out drops the previous owner's Sam state.</summary>
    [Fact]
    public void AccountBoundariesClearSam()
    {
        var shell = Code("AndroidSliceShell.cs");
        Assert.Equal(3, Regex.Matches(shell, @"ClearSamForAccountBoundary\(\);").Count);
        Assert.Contains("DetachSam();", shell);
        Assert.Contains("Post(DetachSam);", shell);
        Assert.Contains("Post(RefreshSamAvailability);", shell);
    }

    private static string Code(string name) => string.Join('\n', File.ReadAllText(Path.Combine(Root, name))
        .Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)
            && !line.TrimStart().StartsWith("///", StringComparison.Ordinal)));

    private static string Root => typeof(AndroidSamContractTests).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "NativeAndroidHeadRoot").Value!;
}
