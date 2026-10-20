using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Data;
using SentenceStudio.Native;

namespace SentenceStudio.Native.Tests;

public sealed class NativeOnboardingWorkflowTests
{
    [Fact]
    public async Task IncompleteOwnedProfileSavesOnlyChosenFieldsAndSkipRoutesDashboard()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserB);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.UserProfiles.SingleAsync(profile =>
                profile.Id == NativeSliceFixture.UserA)).TargetLanguage = "";
            await db.SaveChangesAsync();
        }
        var auth = fixture.Auth;
        Assert.Equal(NativeDestination.Onboarding,
            (await auth.SignInAsync(
                NativeSliceFixture.EmailA, "fixture-password")).Destination);
        Assert.Equal(NativeOnboardingOutcome.Invalid,
            (await auth.SaveOnboardingProfileAsync(
                new NativeOnboardingDraft("", "English", "Korean"))).Outcome);
        Assert.Equal(NativeOnboardingOutcome.Success,
            (await auth.SaveOnboardingProfileAsync(
                new NativeOnboardingDraft("Chosen", "English", "Spanish", 20, "B1")))
                .Outcome);
        Assert.False(fixture.Preferences.Get("is_onboarded", false));
        Assert.Equal(NativeDestination.Onboarding, auth.State.Destination);
        var finished = await auth.FinishOnboardingAsync(createStarter: false);
        Assert.Equal(NativeOnboardingOutcome.Success, finished.Outcome);
        Assert.Equal(1, finished.ContentStepsDone);
        Assert.Equal(NativeDestination.Dashboard, auth.State.Destination);
        Assert.True(auth.State.HasPendingSync);
        Assert.True(fixture.Preferences.Get("is_onboarded", false));

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var owned = await db.UserProfiles.AsNoTracking().SingleAsync(
                profile => profile.Id == NativeSliceFixture.UserA);
            var foreign = await db.UserProfiles.AsNoTracking().SingleAsync(
                profile => profile.Id == NativeSliceFixture.UserB);
            Assert.Equal("Chosen", owned.Name);
            Assert.Equal("Spanish", owned.TargetLanguage);
            Assert.Equal(20, owned.PreferredSessionMinutes);
            Assert.Equal("B1", owned.TargetCEFRLevel);
            Assert.Equal("Korean", foreign.TargetLanguage);
            Assert.Equal(0, await db.LearningResources.CountAsync());
        }
        fixture.Restart();
        Assert.Equal(NativeDestination.Dashboard,
            (await fixture.Auth.RestoreAsync()).Destination);
    }

    [Fact]
    public async Task StarterSetupPersistsOwnedResourceSkillAndMappingsBeforeMarkingOnboarded()
    {
        using var fixture = new NativeSliceFixture(resourceAiEnabled: true);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.UserProfiles.SingleAsync(profile =>
                profile.Id == NativeSliceFixture.UserA)).NativeLanguage = "";
            await db.SaveChangesAsync();
        }
        var auth = fixture.Auth;
        Assert.Equal(NativeDestination.Onboarding,
            (await auth.SignInAsync(
                NativeSliceFixture.EmailA, "fixture-password")).Destination);
        Assert.Equal(NativeOnboardingOutcome.Success,
            (await auth.SaveOnboardingProfileAsync(
                new NativeOnboardingDraft("Beginner", "English", "Korean")))
                .Outcome);
        var result = await auth.FinishOnboardingAsync(createStarter: true);
        Assert.Equal(NativeOnboardingOutcome.Success, result.Outcome);
        Assert.Equal(3, result.ContentStepsDone);
        Assert.Equal(NativeDestination.Dashboard, auth.State.Destination);
        using var scopeCheck = fixture.Scope();
        var dbCheck = scopeCheck.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var starter = await dbCheck.LearningResources.AsNoTracking().SingleAsync(
            resource => resource.UserProfileId == NativeSliceFixture.UserA);
        Assert.Equal("Korean", starter.Language);
        Assert.Equal(2, await dbCheck.ResourceVocabularyMappings.CountAsync(
            mapping => mapping.ResourceId == starter.Id));
        Assert.Equal("Korean", (await dbCheck.SkillProfiles.AsNoTracking()
            .SingleAsync(skill => skill.UserProfileId == NativeSliceFixture.UserA)).Language);
        Assert.Equal(1, fixture.ResourceAi.Calls);
        Assert.Equal(NativeOnboardingOutcome.Blocked,
            (await auth.FinishOnboardingAsync(createStarter: true)).Outcome);
    }

    [Fact]
    public async Task StarterFailureKeepsSetupAvailableAndNeverPretendsSuccess()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.UserProfiles.SingleAsync(profile =>
                profile.Id == NativeSliceFixture.UserA)).TargetLanguage = "";
            await db.SaveChangesAsync();
        }
        var auth = fixture.Auth;
        await auth.SignInAsync(NativeSliceFixture.EmailA, "fixture-password");
        await auth.SaveOnboardingProfileAsync(
            new NativeOnboardingDraft("Owner", "English", "Korean"));

        var failure = await auth.FinishOnboardingAsync(createStarter: true);
        Assert.Equal(NativeOnboardingOutcome.Failed, failure.Outcome);
        Assert.Equal(NativeDestination.Onboarding, auth.State.Destination);
        Assert.False(fixture.Preferences.Get("is_onboarded", false));
        Assert.Equal(NativeOnboardingOutcome.Success,
            (await auth.FinishOnboardingAsync(createStarter: false)).Outcome);
        using var check = fixture.Scope();
        Assert.Empty(await check.ServiceProvider.GetRequiredService<ApplicationDbContext>()
            .LearningResources.ToListAsync());
    }

    [Fact]
    public async Task ConfirmedOwnerWithNoLocalProfileCreatesOnlyItsOwnProfileAndRefusesOwnerSwitch()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserB);
        var auth = fixture.Auth;
        Assert.Equal(NativeDestination.Onboarding,
            (await auth.SignInAsync(
                NativeSliceFixture.EmailA, "fixture-password")).Destination);
        fixture.Activate(NativeSliceFixture.UserB);
        Assert.Equal(NativeOnboardingOutcome.Blocked,
            (await auth.SaveOnboardingProfileAsync(
                new NativeOnboardingDraft("Wrong", "English", "Korean"))).Outcome);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Assert.False(await db.UserProfiles.AnyAsync(user =>
                user.Id == NativeSliceFixture.UserA));
            Assert.Equal(NativeSliceFixture.UserB,
                (await db.UserProfiles.SingleAsync()).Id);
        }
    }
}
