using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native;
using SentenceStudio.Native.iOS.App;
using SentenceStudio.Native.iOS.Features.Skills;
using SentenceStudio.Native.Tests;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.iOS.Tests;

public sealed class NativeSkillsContractTests
{
    [Fact]
    public async Task BlankTitleAndBothCancelPathsNeverWriteToIsolatedSqlite()
    {
        using var fixture = await OwnersAsync();
        using var coordinator = Create(fixture);
        Assert.True(await coordinator.OpenAsync(NativeSliceFixture.UserA));
        Assert.Equal(SkillsListPhase.Empty, coordinator.ListState.Phase);

        coordinator.OpenCreate();
        coordinator.SetTitle("Discard this");
        coordinator.SetDescription("Draft only");
        coordinator.CancelForm();
        Assert.False(coordinator.IsFormOpen);
        Assert.Empty(await OwnedSkillsAsync(fixture, NativeSliceFixture.UserA));

        coordinator.OpenCreate();
        coordinator.SetTitle("   ");
        Assert.False(await coordinator.SaveAsync());
        Assert.Equal("SkillAdd_TitleRequired", coordinator.FormState.ErrorKey);
        Assert.Empty(await OwnedSkillsAsync(fixture, NativeSliceFixture.UserA));

        coordinator.SetTitle("Writing");
        coordinator.SetDescription("Practice written Korean");
        Assert.True(await coordinator.SaveAsync());
        var saved = Assert.Single(await OwnedSkillsAsync(
            fixture, NativeSliceFixture.UserA));
        Assert.Equal("Writing", saved.Title);
        Assert.Equal("Practice written Korean", saved.Description);
        Assert.False(saved.IsArchived);
        Assert.True(saved.CreatedAt != default);
        Assert.True(saved.UpdatedAt != default);

        await coordinator.OpenEditAsync(saved.Id);
        coordinator.SetTitle("Discard this edit");
        coordinator.SetDescription("Discard this description");
        coordinator.CancelForm();
        await coordinator.OpenEditAsync(saved.Id);
        Assert.Equal("Writing", coordinator.FormState.Title);
        Assert.Equal("Practice written Korean", coordinator.FormState.Description);
        coordinator.RequestDelete();
        Assert.True(coordinator.IsDeleteConfirmationPresented);
        coordinator.CancelDelete();
        Assert.False(coordinator.IsDeleteConfirmationPresented);
        Assert.False(await coordinator.ConfirmDeleteAsync());
        coordinator.CancelForm();
        Assert.Equal("Writing", Assert.Single(await OwnedSkillsAsync(
            fixture, NativeSliceFixture.UserA)).Title);
        Assert.Empty(await OwnedSkillsAsync(fixture, NativeSliceFixture.UserB));
        await AssertUnrelatedTablesEmptyAsync(fixture);
    }

    [Fact]
    public async Task AddEditReturnToSelectorAndRestartPreserveOnlyOwnedRows()
    {
        using var fixture = await OwnersAsync();
        var foreign = new SkillProfile
        {
            Id = "foreign-skill", Title = "Keep other owner",
            Description = "Unrelated description",
            UserProfileId = NativeSliceFixture.UserB
        };
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.SkillProfiles.Add(foreign);
            await db.SaveChangesAsync();
        }

        string savedId;
        var dashboard = fixture.Dashboard;
        using (var coordinator = Create(fixture))
        {
            Assert.True(await coordinator.OpenAsync(NativeSliceFixture.UserA));
            coordinator.OpenCreate();
            coordinator.SetTitle("Conversation");
            coordinator.SetDescription("Daily speaking");
            Assert.True(await coordinator.SaveAsync());
            savedId = Assert.Single(coordinator.ListState.Profiles).Id;
            Assert.Equal("SkillAdd_SavedSuccess",
                coordinator.ListState.FeedbackKey);
            coordinator.Close();

            dashboard.SetMode(NativeDashboardMode.ChooseOwn);
            var selector = await dashboard.LoadChooseOwnAsync();
            Assert.Equal(savedId, Assert.Single(selector.ChooseOwn!.Skills).Id);
            Assert.DoesNotContain(selector.ChooseOwn.Skills,
                skill => skill.Id == foreign.Id);
            Assert.Equal(savedId,
                dashboard.SelectChooseOwn([], savedId).ChooseOwn?.SelectedSkillId);

            Assert.True(await coordinator.OpenAsync(NativeSliceFixture.UserA));
            await coordinator.OpenEditAsync(savedId);
            Assert.Equal(SkillFormPhase.Editing, coordinator.FormState.Phase);
            Assert.NotNull(coordinator.FormState.CreatedAt);
            Assert.NotNull(coordinator.FormState.UpdatedAt);
            coordinator.SetTitle("Discarded");
            coordinator.CancelForm();
            Assert.Equal("Conversation",
                Assert.Single(await OwnedSkillsAsync(
                    fixture, NativeSliceFixture.UserA)).Title);

            await coordinator.OpenEditAsync(savedId);
            coordinator.SetTitle("Conversation in Korean");
            coordinator.SetDescription("Dialogue and turn-taking");
            Assert.True(await coordinator.SaveAsync());
            Assert.Equal("SkillEdit_SavedSuccess",
                coordinator.ListState.FeedbackKey);
            Assert.Equal(savedId, Assert.Single(
                await OwnedSkillsAsync(fixture, NativeSliceFixture.UserA)).Id);
            coordinator.Close();
        }

        var updatedSelector = await dashboard.LoadChooseOwnAsync();
        var option = Assert.Single(updatedSelector.ChooseOwn!.Skills);
        Assert.Equal(savedId, option.Id);
        Assert.Equal("Conversation in Korean", option.Title);
        Assert.Equal(savedId, updatedSelector.ChooseOwn.SelectedSkillId);

        fixture.Restart();
        fixture.Activate(NativeSliceFixture.UserA);
        using (var restored = Create(fixture))
        {
            Assert.True(await restored.OpenAsync(NativeSliceFixture.UserA));
            var row = Assert.Single(restored.ListState.Profiles);
            Assert.Equal(savedId, row.Id);
            Assert.Equal("Conversation in Korean", row.Title);
            Assert.Equal("Dialogue and turn-taking", row.Description);
            await restored.OpenEditAsync(savedId);
            Assert.Equal("Conversation in Korean", restored.FormState.Title);
            restored.CancelForm();
            restored.Close();
        }

        var restoredDashboard = fixture.Dashboard;
        restoredDashboard.SetMode(NativeDashboardMode.ChooseOwn);
        var afterRestart = await restoredDashboard.LoadChooseOwnAsync();
        Assert.Equal(savedId, Assert.Single(afterRestart.ChooseOwn!.Skills).Id);
        Assert.Equal("Conversation in Korean",
            Assert.Single(afterRestart.ChooseOwn.Skills).Title);
        Assert.Equal("Keep other owner",
            Assert.Single(await OwnedSkillsAsync(
                fixture, NativeSliceFixture.UserB)).Title);
        Assert.Equal("Unrelated description",
            Assert.Single(await OwnedSkillsAsync(
                fixture, NativeSliceFixture.UserB)).Description);
        await AssertUnrelatedTablesEmptyAsync(fixture);
    }

    [Fact]
    public async Task MissingForeignAndRevokedOwnersFailClosedWithoutMutation()
    {
        using var fixture = await OwnersAsync();
        var foreign = new SkillProfile
        {
            Id = "foreign-skill", Title = "Other user",
            UserProfileId = NativeSliceFixture.UserB
        };
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.SkillProfiles.Add(foreign);
            await db.SaveChangesAsync();
        }

        using var coordinator = Create(fixture);
        Assert.False(await coordinator.OpenAsync(null));
        Assert.True(await coordinator.OpenAsync(NativeSliceFixture.UserA));
        Assert.Empty(coordinator.ListState.Profiles);
        await coordinator.OpenEditAsync(foreign.Id);
        Assert.Equal(SkillFormPhase.NotFound, coordinator.FormState.Phase);
        Assert.Equal("SkillEdit_NotFound", coordinator.FormState.ErrorKey);
        Assert.Equal(string.Empty, coordinator.FormState.Title);
        coordinator.CancelForm();
        coordinator.OpenCreate();
        coordinator.SetTitle("Must not be saved");
        fixture.Activate(NativeSliceFixture.UserB);
        Assert.False(await coordinator.SaveAsync());
        Assert.Equal(SkillsListPhase.Blocked, coordinator.ListState.Phase);
        Assert.Empty(coordinator.ListState.Profiles);
        Assert.False(coordinator.IsFormOpen);
        Assert.Empty(await OwnedSkillsAsync(fixture, NativeSliceFixture.UserA));
        Assert.Equal("Other user", Assert.Single(
            await OwnedSkillsAsync(fixture, NativeSliceFixture.UserB)).Title);
        coordinator.Close();
        fixture.LoseOwner();
        Assert.False(await coordinator.OpenAsync(NativeSliceFixture.UserA));
        await AssertUnrelatedTablesEmptyAsync(fixture);
    }

    [Fact]
    public async Task ConfirmedDeleteHardDeletesOnlyDisposableOwnedSkill()
    {
        using var fixture = await OwnersAsync();
        fixture.Activate(NativeSliceFixture.UserA);
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.SkillProfiles.Add(new SkillProfile
            {
                Id = "other-owner-skill", Title = "Never delete this",
                UserProfileId = NativeSliceFixture.UserB
            });
            await db.SaveChangesAsync();
        }
        var dashboard = fixture.Dashboard;
        using (var coordinator = Create(fixture))
        {
            Assert.True(await coordinator.OpenAsync(NativeSliceFixture.UserA));
            coordinator.OpenCreate();
            coordinator.SetTitle("Disposable skill");
            Assert.True(await coordinator.SaveAsync());
            var ownId = Assert.Single(coordinator.ListState.Profiles).Id;
            dashboard.SetMode(NativeDashboardMode.ChooseOwn);
            await dashboard.LoadChooseOwnAsync();
            Assert.Equal(ownId,
                dashboard.SelectChooseOwn([], ownId).ChooseOwn?.SelectedSkillId);
            await coordinator.OpenEditAsync(ownId);
            coordinator.RequestDelete();
            Assert.True(await coordinator.ConfirmDeleteAsync());
            Assert.False(coordinator.IsFormOpen);
            Assert.Equal(SkillsListPhase.Empty, coordinator.ListState.Phase);
            Assert.Equal("SkillEdit_DeletedSuccess",
                coordinator.ListState.FeedbackKey);
            Assert.Empty(await OwnedSkillsAsync(
                fixture, NativeSliceFixture.UserA));
            coordinator.Close();
        }
        var reloaded = await dashboard.LoadChooseOwnAsync();
        Assert.Empty(reloaded.ChooseOwn!.Skills);
        Assert.Null(reloaded.ChooseOwn.SelectedSkillId);
        Assert.Equal("Never delete this",
            Assert.Single(await OwnedSkillsAsync(
                fixture, NativeSliceFixture.UserB)).Title);
        await AssertUnrelatedTablesEmptyAsync(fixture);
    }

    [Fact]
    public async Task SkillsOpenFromResourcesNeverClaimsTheResourceFormRoute()
    {
        Assert.True(ApplicationFormRoutes.ShouldPopResourceForm(
            true, [ApplicationFormRoutes.ResourceForm]));
        Assert.False(ApplicationFormRoutes.ShouldPopResourceForm(
            false, [ApplicationFormRoutes.ResourceForm]));
        Assert.False(ApplicationFormRoutes.ShouldPopResourceForm(
            true, [ApplicationFormRoutes.Skills]));
        Assert.False(ApplicationFormRoutes.ShouldPopResourceForm(
            true, [ApplicationFormRoutes.Skills, ApplicationFormRoutes.SkillForm]));
        Assert.False(ApplicationFormRoutes.ShouldPopResourceForm(
            true, [ApplicationFormRoutes.ResourceForm, ApplicationFormRoutes.Skills]));

        using var fixture = await OwnersAsync();
        using var coordinator = Create(fixture);
        var resourcesPath = new List<string>();
        var callbacks = new List<(SkillsListPhase Phase, string[] Path, bool Pop)>();
        coordinator.Changed += () =>
        {
            callbacks.Add((
                coordinator.ListState.Phase,
                resourcesPath.ToArray(),
                ApplicationFormRoutes.ShouldPopResourceForm(
                    formMounted: false, resourcesPath)));
            if (coordinator.IsOpen && resourcesPath.Count == 0)
                resourcesPath.Add(ApplicationFormRoutes.Skills);
        };

        Assert.True(await coordinator.OpenAsync(NativeSliceFixture.UserA));
        Assert.Contains(callbacks, update =>
            update.Phase == SkillsListPhase.Loading
            && update.Path.SequenceEqual([ApplicationFormRoutes.Skills])
            && !update.Pop);
        Assert.All(callbacks, update => Assert.False(update.Pop));
        Assert.Equal([ApplicationFormRoutes.Skills], resourcesPath);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkillsMutationDefersFormPopUntilPendingClears(
        bool delete)
    {
        using var fixture = await OwnersAsync();
        const string targetId = "transition-test-skill";
        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.SkillProfiles.Add(new SkillProfile
            {
                Id = "foreign-transition-skill",
                Title = "Keep other owner",
                UserProfileId = NativeSliceFixture.UserB
            });
            if (delete)
                db.SkillProfiles.Add(new SkillProfile
                {
                    Id = targetId,
                    Title = "Delete only my row",
                    UserProfileId = NativeSliceFixture.UserA
                });
            await db.SaveChangesAsync();
        }

        using var coordinator = Create(fixture);
        var resourcesPath = new List<string>();
        var updates = new List<SkillsRouteSnapshot>();
        var formMounted = false;
        var popCount = 0;
        var cancelRefusedWhilePending = false;
        coordinator.Changed += () =>
        {
            Assert.False(ApplicationFormRoutes.ShouldPopResourceForm(
                formMounted: false, resourcesPath));
            if (coordinator.IsOpen && resourcesPath.Count == 0)
                resourcesPath.Add(ApplicationFormRoutes.Skills);
            if (coordinator.IsFormOpen && !formMounted
                && resourcesPath.Count == 1)
            {
                resourcesPath.Add(ApplicationFormRoutes.SkillForm);
                formMounted = true;
            }
            if (coordinator.IsPending && coordinator.IsFormOpen)
            {
                coordinator.CancelForm();
                cancelRefusedWhilePending |= coordinator.IsFormOpen;
            }
            var canNavigateBack = !coordinator.IsPending;
            var pop = ApplicationFormRoutes.ShouldPopSkillForm(
                coordinator.IsFormOpen, formMounted, canNavigateBack,
                resourcesPath);
            updates.Add(new SkillsRouteSnapshot(
                coordinator.IsPending, coordinator.IsFormOpen,
                coordinator.ListState.Phase, coordinator.FormState.Phase,
                coordinator.FormVersion, resourcesPath.ToArray(), pop));
            if (pop)
            {
                resourcesPath.RemoveAt(resourcesPath.Count - 1);
                formMounted = false;
                popCount++;
            }
        };

        Assert.True(await coordinator.OpenAsync(NativeSliceFixture.UserA));
        if (delete)
        {
            await coordinator.OpenEditAsync(targetId);
            coordinator.RequestDelete();
            Assert.True(coordinator.IsDeleteConfirmationPresented);
        }
        else
        {
            coordinator.OpenCreate();
            coordinator.SetTitle("Only one saved skill");
            coordinator.SetDescription("No duplicate writes");
        }
        Assert.True(formMounted);
        Assert.Equal([ApplicationFormRoutes.Skills,
            ApplicationFormRoutes.SkillForm], resourcesPath);

        var mutationStart = updates.Count;
        var beforeFinalFormVersion = coordinator.FormVersion;
        Assert.True(delete
            ? await coordinator.ConfirmDeleteAsync()
            : await coordinator.SaveAsync());
        var mutationUpdates = updates.Skip(mutationStart).ToArray();
        Assert.Contains(mutationUpdates, update =>
            update.Pending && !update.FormOpen && !update.Pop
            && update.ListPhase == SkillsListPhase.Loading
            && update.Path.SequenceEqual(
                [ApplicationFormRoutes.Skills, ApplicationFormRoutes.SkillForm]));
        Assert.Equal(1, popCount);
        Assert.True(cancelRefusedWhilePending);
        Assert.False(coordinator.IsPending);
        Assert.False(coordinator.IsFormOpen);
        Assert.False(formMounted);
        Assert.Equal([ApplicationFormRoutes.Skills], resourcesPath);
        Assert.Equal(mutationUpdates[^1].FormVersion, coordinator.FormVersion);
        Assert.True(coordinator.FormVersion > beforeFinalFormVersion);
        Assert.False(mutationUpdates[^1].Pending);
        Assert.True(mutationUpdates[^1].Pop);
        Assert.DoesNotContain(mutationUpdates, update => update.FormPhase is
            SkillFormPhase.SaveError or SkillFormPhase.DeleteError);

        if (delete)
        {
            Assert.False(await coordinator.ConfirmDeleteAsync());
            Assert.Empty(await OwnedSkillsAsync(fixture, NativeSliceFixture.UserA));
            Assert.Equal("SkillEdit_DeletedSuccess",
                coordinator.ListState.FeedbackKey);
        }
        else
        {
            Assert.False(await coordinator.SaveAsync());
            Assert.Equal("Only one saved skill", Assert.Single(
                await OwnedSkillsAsync(fixture, NativeSliceFixture.UserA)).Title);
            Assert.Equal("SkillAdd_SavedSuccess",
                coordinator.ListState.FeedbackKey);
        }
        Assert.Equal("Keep other owner", Assert.Single(
            await OwnedSkillsAsync(fixture, NativeSliceFixture.UserB)).Title);
        await AssertUnrelatedTablesEmptyAsync(fixture);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PostCommitProjectionFaultDoesNotMislabelTheWrite(
        bool delete)
    {
        using var fixture = await OwnersAsync();
        using var coordinator = Create(fixture);
        Assert.True(await coordinator.OpenAsync(NativeSliceFixture.UserA));
        if (delete)
        {
            using (var scope = fixture.Scope())
            {
                var db = scope.ServiceProvider
                    .GetRequiredService<ApplicationDbContext>();
                db.SkillProfiles.Add(new SkillProfile
                {
                    Id = "projection-fault-skill",
                    Title = "Delete exactly once",
                    UserProfileId = NativeSliceFixture.UserA
                });
                await db.SaveChangesAsync();
            }
            await coordinator.OpenEditAsync("projection-fault-skill");
            coordinator.RequestDelete();
        }
        else
        {
            coordinator.OpenCreate();
            coordinator.SetTitle("Persist exactly once");
        }

        var faulted = false;
        var sawFinalPublish = false;
        coordinator.Changed += () =>
        {
            if (!faulted && coordinator.IsPending && !coordinator.IsFormOpen)
            {
                faulted = true;
                throw new InvalidOperationException("Projection fault after mutation");
            }
            if (faulted && !coordinator.IsPending)
                sawFinalPublish = true;
        };

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (delete)
                await coordinator.ConfirmDeleteAsync();
            else
                await coordinator.SaveAsync();
        });
        Assert.True(faulted);
        Assert.True(sawFinalPublish);
        Assert.False(coordinator.IsPending);
        Assert.False(coordinator.IsFormOpen);
        Assert.Null(coordinator.FormState.ErrorKey);
        Assert.Equal(delete ? "SkillEdit_DeletedSuccess" : "SkillAdd_SavedSuccess",
            coordinator.ListState.FeedbackKey);
        Assert.Equal(delete ? 0 : 1,
            (await OwnedSkillsAsync(fixture, NativeSliceFixture.UserA)).Count);
        Assert.False(delete
            ? await coordinator.ConfirmDeleteAsync()
            : await coordinator.SaveAsync());
    }

    [Fact]
    public void SkillScreensAreRealSwiftUIRoutesWithBothCompactEntries()
    {
        var scene = Read("App/ApplicationScene.cs");
        var controller = Read("App/ApplicationController.cs");
        var dashboard = Read("Features/Dashboard/DashboardView.cs");
        var resources = Read("Features/LearningResources/LearningResourcesView.cs");
        var list = Read("Features/Skills/SkillsView.cs");
        var form = Read("Features/Skills/SkillProfileFormView.cs");

        Assert.Contains("stack.Push(\"skills\", _skillsSlot)", scene);
        Assert.Contains("stack.Push(\"skill-form\",", scene);
        Assert.Contains("ApplicationFormRoutes.ShouldPopResourceForm(", scene);
        Assert.Contains("ApplicationFormRoutes.ShouldPopSkillForm(", scene);
        Assert.Contains("_app.OnSkillsPopped()", scene);
        Assert.Contains("_app.OnSkillFormPopped()", scene);
        Assert.Contains("Views.ResourceDeleteConfirmation(", scene);
        Assert.Equal(2, scene.Split(".WithTransparentDeleteBackdrop()").Length - 1);
        Assert.Contains("\"SkillEdit_DeleteMessage\"", scene);
        Assert.Contains("ReloadChooseOwnAfterSkills()", controller);
        Assert.Contains("text(\"Nav_Skills\"), actions.OpenSkills", dashboard);
        Assert.Contains("text(\"Nav_Skills\"), openSkills", resources);
        Assert.Contains("Views.LazyVGrid(", list);
        Assert.Contains("Views.TextField(", form);
        Assert.Contains("Views.TextEditor(", form);
        Assert.Contains("native.skills.form.cancel", form);
        Assert.DoesNotContain("using UIKit;", scene);
        Assert.DoesNotContain("using UIKit;", list);
        Assert.DoesNotContain("using UIKit;", form);
    }

    private static async Task<NativeSliceFixture> OwnersAsync()
    {
        var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(NativeSliceFixture.UserA);
        await fixture.SeedProfileAsync(NativeSliceFixture.UserB);
        fixture.Activate(NativeSliceFixture.UserA);
        return fixture;
    }

    private static SkillsCoordinator Create(NativeSliceFixture fixture) =>
        new(fixture.Provider.GetRequiredService<SkillProfileRepository>(),
            fixture.UserScope, NullLogger<SkillsCoordinator>.Instance);

    private static async Task<List<SkillProfile>> OwnedSkillsAsync(
        NativeSliceFixture fixture, string owner)
    {
        using var scope = fixture.Scope();
        return await scope.ServiceProvider
            .GetRequiredService<ApplicationDbContext>()
            .SkillProfiles.AsNoTracking()
            .Where(skill => skill.UserProfileId == owner)
            .ToListAsync();
    }

    private static async Task AssertUnrelatedTablesEmptyAsync(
        NativeSliceFixture fixture)
    {
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(2, await db.UserProfiles.CountAsync());
        Assert.Equal(0, await db.LearningResources.CountAsync());
        Assert.Equal(0, await db.VocabularyWords.CountAsync());
        Assert.Equal(0, await db.VocabularyProgresses.CountAsync());
        Assert.Equal(0, await db.DailyPlans.CountAsync());
        Assert.Equal(0, await db.ActivitySessions.CountAsync());
        Assert.Equal(0, await db.StreamHistories.CountAsync());
    }

    private static string Read(string file) => File.ReadAllText(
        Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(CurrentFile())!,
            "../../src/SentenceStudio.Native.iOS", file)));

    private static string CurrentFile([CallerFilePath] string path = "") => path;

    private sealed record SkillsRouteSnapshot(
        bool Pending, bool FormOpen, SkillsListPhase ListPhase,
        SkillFormPhase FormPhase, long FormVersion, string[] Path, bool Pop);
}
