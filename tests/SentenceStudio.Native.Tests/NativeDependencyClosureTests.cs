using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Native;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

public sealed class NativeDependencyClosureTests
{
    [Fact]
    public async Task ProductionDefaultPorts_RefuseUnprovenWritesAndOneOptionQuiz()
    {
        var services = new ServiceCollection();
        services.AddSentenceStudioNativeSlice();
        using var provider = services.BuildServiceProvider();

        var creation = provider.GetRequiredService<INativePlanCreation>();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            creation.CreateIfAbsentAsync(
                "owner", new DateOnly(2026, 9, 28), CancellationToken.None));
        var attempt = provider.GetRequiredService<INativeQuizAttemptCommitter>();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            attempt.FindAsync("owner", "plan=context", "turn", CancellationToken.None));
        var choices = provider.GetRequiredService<INativeQuizChoices>();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            choices.CreateAsync(
                "owner",
                new VocabularyWord { TargetLanguageTerm = "target", NativeLanguageTerm = "native" },
                [],
                promptUsesNativeLanguage: false,
                CancellationToken.None));
        var eligibility = provider.GetRequiredService<INativeQuizEligibility>();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            eligibility.SelectAsync("owner", false, [], CancellationToken.None));
        var grading = provider.GetRequiredService<INativeQuizGrader>();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            grading.EvaluateAsync(
                "owner", "MultipleChoice", "native", "native", CancellationToken.None));
    }

    [Fact]
    public void PlainNet11NativeProject_ResolvesNoMauiCometOrPlatformUiPackages()
    {
        var testsDirectory = Path.GetDirectoryName(SourceFile())!;
        var assetsFile = Path.GetFullPath(Path.Combine(testsDirectory,
            "../../src/SentenceStudio.Native/obj/project.assets.json"));
        Assert.True(File.Exists(assetsFile), "Build the native project before running its tests.");

        using var assets = JsonDocument.Parse(File.ReadAllText(assetsFile));
        var targets = assets.RootElement.GetProperty("targets").EnumerateObject().ToList();
        Assert.Contains(targets, target => target.Name == "net11.0");
        foreach (var target in targets)
        {
            Assert.StartsWith("net11.0", target.Name);
            var names = target.Value.EnumerateObject()
                .Select(library => library.Name)
                .ToList();
            Assert.Contains(names, name => name.StartsWith(
                "SentenceStudio.AppLib/", StringComparison.Ordinal));
            Assert.Contains(names, name => name.StartsWith(
                "SentenceStudio.Shared/", StringComparison.Ordinal));
            Assert.DoesNotContain(names, name =>
                name.Contains("maui", StringComparison.OrdinalIgnoreCase)
                || name.Contains("comet", StringComparison.OrdinalIgnoreCase)
                || name.Contains("swiftui", StringComparison.OrdinalIgnoreCase)
                || name.Contains("androidx.compose", StringComparison.OrdinalIgnoreCase));
        }
        Assert.DoesNotContain(typeof(NativeQuizPresenter).Assembly
            .GetReferencedAssemblies().Select(assembly => assembly.Name),
            name => name is not null &&
                (name.Contains("maui", StringComparison.OrdinalIgnoreCase)
                 || name.Contains("comet", StringComparison.OrdinalIgnoreCase)));
    }

    private static string SourceFile([CallerFilePath] string path = "") => path;
}
