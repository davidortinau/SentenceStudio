using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>
/// Found with issue 233: a resource saved with the language "Unknown" made the sentence
/// prompt ask for "Unknown" sentences. It now takes the learner's target language.
/// </summary>
public sealed class TranslationServiceTargetLanguageTests
{
    [Theory]
    [InlineData("Unknown", "Korean")]
    [InlineData("unknown", "Korean")]
    [InlineData("", "Korean")]
    [InlineData(null, "Korean")]
    [InlineData("Japanese", "Japanese")]
    public async Task SentencePrompt_NamesTheResourceLanguage_OrTheProfilesWhenTheResourceHasNone(
        string? resourceLanguage, string expected)
    {
        await using var fixture = await ActivityPageFixture.CreateAsync(services =>
            services.AddSingleton<IFileSystemService>(new TargetLanguageTemplate()));
        var (resource, words) = await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("바다", "sea"));
        await using (var scope = fixture.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.LearningResources.Where(row => row.Id == resource.Id)
                .ExecuteUpdateAsync(row => row.SetProperty(r => r.Language, resourceLanguage));
        }
        fixture.Ai.Respond = _ => new TranslationResponse();
        var service = fixture.Services.GetRequiredService<TranslationService>();

        await service.GetTranslationSentences(resource.Id, 5, string.Empty, [words[0].Id]);

        fixture.Ai.Prompts.Should().ContainSingle().Which.Should().Be($"target={expected}");
    }

    /// <summary>The sentence prompt reduced to the one value under test.</summary>
    private sealed class TargetLanguageTemplate : IFileSystemService
    {
        public string AppDataDirectory => Path.GetTempPath();

        public Task<Stream> OpenAppPackageFileAsync(string filename) =>
            Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes("target={{ target_language }}")));
    }
}
