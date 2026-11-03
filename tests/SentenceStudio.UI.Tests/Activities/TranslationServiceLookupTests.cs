using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.UI.Tests.Activities;

/// <summary>Backports B7/B8: the shared translate helper reads the owner's words and saves nothing.</summary>
public sealed class TranslationServiceLookupTests
{
    [Fact]
    public async Task TranslateAsync_UsesTheOwnersWordAndNeverAnotherOwners()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        await fixture.SeedResourceAsync(ActivityPageFixture.OwnerA, ("바다", "sea"));
        await fixture.SeedResourceAsync(ActivityPageFixture.OwnerB, ("하늘", "B's sky"));
        fixture.Ai.Respond = _ => "sky";
        var service = fixture.Services.GetRequiredService<TranslationService>();

        (await service.TranslateAsync("바다", userProfileId: ActivityPageFixture.OwnerA)).Should().Be("sea");
        (await service.TranslateAsync("sea", userProfileId: ActivityPageFixture.OwnerA)).Should().Be("바다");
        fixture.Ai.Prompts.Should().BeEmpty();
        (await service.TranslateAsync("하늘", userProfileId: ActivityPageFixture.OwnerA)).Should().Be("sky");
        fixture.Ai.Prompts.Should().ContainSingle();
    }

    [Fact]
    public async Task TranslateAsync_AiResult_IsNotSavedAsAVocabularyWord()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        fixture.Ai.Respond = _ => "sea";
        var service = fixture.Services.GetRequiredService<TranslationService>();

        (await service.TranslateAsync("바다", userProfileId: ActivityPageFixture.OwnerA)).Should().Be("sea");

        (await fixture.RowsAsync<VocabularyWord>()).Should().BeEmpty();
    }

    [Fact]
    public async Task TranslateAsync_WithoutAnOwner_ReturnsNothingAndCallsNoAi()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        fixture.Preferences.ActiveId = string.Empty;
        var service = fixture.Services.GetRequiredService<TranslationService>();

        (await service.TranslateAsync("바다")).Should().BeEmpty();
        fixture.Ai.Prompts.Should().BeEmpty();
    }

    [Fact]
    public async Task TranslateAsync_AiFailure_Throws()
    {
        await using var fixture = await ActivityPageFixture.CreateAsync();
        fixture.Ai.Respond = _ => throw new HttpRequestException("AI unavailable");
        var service = fixture.Services.GetRequiredService<TranslationService>();

        var act = () => service.TranslateAsync("바다", userProfileId: ActivityPageFixture.OwnerA);

        await act.Should().ThrowAsync<HttpRequestException>("a failure is reported, not returned as an empty result");
    }
}
