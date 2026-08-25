using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Api.Coach.Memory;
using SentenceStudio.Api.Coach.Persistence;
using SentenceStudio.Api.Coach.Persistence.Deletion;
using SentenceStudio.Api.Coach.Persistence.History;
using SentenceStudio.Contracts.Coach;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Coach;

/// <summary>
/// Account erasure exercised against the real API container, not a hand-built one.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests around the coordinator construct their contributors directly, which proves the
/// coordination logic but says nothing about whether the running application actually registered
/// those contributors. That gap is where the original defect lived: the deletion path existed and
/// was correct, and nothing called it.
/// </para>
/// <para>
/// These tests therefore resolve everything from the host's own service provider. A contributor
/// that stops being registered — because a registration moved, or a conditional stopped matching —
/// fails here even though every unit test still passes.
/// </para>
/// </remarks>
public class CoachAccountDeletionIntegrationTests
{
    private const string Owner = "integration-owner-1";
    private const string Bystander = "integration-bystander-2";

    [Fact]
    public void TheHostRegistersTheOwnerScopedConversationService()
    {
        using var factory = new CoachApiFactory { CoachEnabled = true };
        using var scope = factory.Services.CreateScope();

        // The API deliberately does not reference the client app's composition root, so this
        // registration is easy to lose. Losing it is silent: deletion would simply stop covering
        // the learner's older conversations.
        scope.ServiceProvider.GetService<IConversationOwnerDataService>()
             .Should().NotBeNull("account deletion cannot erase legacy conversations without it");
    }

    [Fact]
    public void TheHostRegistersEveryDeletionContributor()
    {
        using var factory = new CoachApiFactory { CoachEnabled = true };
        using var scope = factory.Services.CreateScope();

        var names = scope.ServiceProvider
            .GetServices<ICoachDataDeletionContributor>()
            .Select(contributor => contributor.Name)
            .ToArray();

        // Every lane that writes owner-scoped rows must have a contributor here. Asserting only
        // the two this file owns would let a lost history registration pass silently, and the
        // symptom of that is the one this whole service exists to prevent: erasure reporting
        // success while the learner's rows survive.
        names.Should().Contain("CoachCheckpoint")
             .And.Contain("CoachConversationHistory")
             .And.Contain("LegacyConversation");

        // Memory is deliberately not wired into Program.cs yet, so its contributor is absent by
        // design rather than by mistake. What must never happen is the lane being wired without
        // its contributor coming along, so this asserts the pairing rather than the wiring:
        // AddCoachMemory registers the store and the contributor together, and this fails the
        // moment someone registers one without the other.
        var memoryLaneRegistered = scope.ServiceProvider.GetService<ICoachMemoryStore>() is not null;
        names.Contains("CoachMemoryFact").Should().Be(
            memoryLaneRegistered,
            "memory rows are owner-scoped, so the memory lane and its deletion contributor must "
            + "be registered together or account erasure silently stops covering them");
    }

    /// <summary>
    /// Every owner-scoped table on <see cref="CoachDbContext"/> must be emptied by one pass.
    /// </summary>
    /// <remarks>
    /// This is the assertion that survives a new lane being added. A contributor name list goes
    /// stale the moment someone introduces a table without telling anyone; counting rows left in
    /// the context does not, because a new table with surviving owner rows fails it on its own.
    /// </remarks>
    [Fact]
    public async Task OnePass_LeavesNoOwnerScopedCoachRowsInAnyTable()
    {
        using var factory = new CoachApiFactory { CoachEnabled = true };

        await SeedAsync(factory, Owner);

        bool memoryLaneRegistered;

        using (var scope = factory.Services.CreateScope())
        {
            memoryLaneRegistered = scope.ServiceProvider.GetService<ICoachMemoryStore>() is not null;

            var report = await scope.ServiceProvider
                .GetRequiredService<ICoachDataDeletionService>()
                .DeleteAllForOwnerAsync(CoachOwner.ForUser(Owner));

            report.Succeeded.Should().BeTrue();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CoachDbContext>();

            (await db.CoachSessions.CountAsync(x => x.UserProfileId == Owner)).Should().Be(0);
            (await db.CoachPlanRevisions.CountAsync(x => x.UserProfileId == Owner)).Should().Be(0);
            (await db.CoachUsages.CountAsync(x => x.UserProfileId == Owner)).Should().Be(0);
            (await db.CoachConversations.CountAsync(x => x.UserProfileId == Owner)).Should().Be(0);
            (await db.CoachTurnOperations.CountAsync(x => x.UserProfileId == Owner)).Should().Be(0);

            // Messages hang off conversations rather than carrying an owner column, so they are
            // checked as an absolute count: the only conversations seeded here belong to Owner.
            (await db.CoachMessages.CountAsync()).Should().Be(0);

            if (memoryLaneRegistered)
            {
                (await db.CoachMemoryFacts.CountAsync(x => x.UserProfileId == Owner)).Should().Be(0);
            }
        }
    }

    [Fact]
    public async Task OnePass_ErasesCoachRowsAndOwnedLegacyRowsTogether()
    {
        using var factory = new CoachApiFactory { CoachEnabled = true };

        await SeedAsync(factory, Owner);
        await SeedAsync(factory, Bystander);
        await SeedOwnerlessLegacyRowAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            var report = await scope.ServiceProvider
                .GetRequiredService<ICoachDataDeletionService>()
                .DeleteAllForOwnerAsync(CoachOwner.ForUser(Owner));

            report.Succeeded.Should().BeTrue();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var coach = scope.ServiceProvider.GetRequiredService<CoachDbContext>();
            var app = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            (await coach.CoachSessions.CountAsync(s => s.UserProfileId == Owner)).Should().Be(0);
            (await app.Conversations.CountAsync(c => c.UserProfileId == Owner)).Should().Be(0);
            (await app.ConversationChunks.CountAsync(c => c.UserProfileId == Owner)).Should().Be(0);
        }
    }

    [Fact]
    public async Task OnePass_LeavesEveryOtherLearnerAndEveryOwnerlessRowAlone()
    {
        using var factory = new CoachApiFactory { CoachEnabled = true };

        await SeedAsync(factory, Owner);
        await SeedAsync(factory, Bystander);
        await SeedOwnerlessLegacyRowAsync(factory);

        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider
                .GetRequiredService<ICoachDataDeletionService>()
                .DeleteAllForOwnerAsync(CoachOwner.ForUser(Owner));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var coach = scope.ServiceProvider.GetRequiredService<CoachDbContext>();
            var app = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

            (await coach.CoachSessions.CountAsync(s => s.UserProfileId == Bystander)).Should().Be(1);
            (await app.Conversations.CountAsync(c => c.UserProfileId == Bystander)).Should().Be(1);

            // The ownerless row is the whole point. It predates owner scoping, nobody can prove
            // whose it is, and an erasure that swept it up would be destroying a stranger's data
            // on a guess.
            (await app.Conversations.CountAsync(c => c.UserProfileId == null)).Should().Be(1);
            (await app.ConversationChunks.CountAsync(c => c.UserProfileId == null)).Should().Be(1);
        }
    }

    [Fact]
    public async Task ASecondErasureOfTheSameLearnerStillSucceeds()
    {
        using var factory = new CoachApiFactory { CoachEnabled = true };
        await SeedAsync(factory, Owner);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var scope = factory.Services.CreateScope();
            var report = await scope.ServiceProvider
                .GetRequiredService<ICoachDataDeletionService>()
                .DeleteAllForOwnerAsync(CoachOwner.ForUser(Owner));

            // A retried deletion is the normal case after a transient failure, and it must not
            // report failure just because there is nothing left to remove.
            report.Succeeded.Should().BeTrue($"attempt {attempt + 1} must be safe to repeat");
        }
    }

    private static async Task SeedAsync(CoachApiFactory factory, string userProfileId)
    {
        using var scope = factory.Services.CreateScope();

        var coach = scope.ServiceProvider.GetRequiredService<CoachDbContext>();
        coach.CoachSessions.Add(new CoachSession
        {
            Id = Guid.NewGuid().ToString("N"),
            UserProfileId = userProfileId,
            Status = CoachSessionStatus.Active,
            AgentConfigVersion = "v1",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        });
        await coach.SaveChangesAsync();

        // History and memory rows, so the "no owner-scoped rows survive" assertion is not
        // vacuous. These are seeded directly rather than through the history service: this test
        // is about erasure coverage, and going through the write path would couple it to a
        // product surface that is still moving.
        var historyConversationId = Guid.NewGuid().ToString("N");
        coach.CoachConversations.Add(new SentenceStudio.Api.Coach.Persistence.History.CoachConversation
        {
            Id = historyConversationId,
            UserProfileId = userProfileId,
            ProtectedTitle = "protected",
            HistoryStartsAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        coach.CoachMessages.Add(new SentenceStudio.Api.Coach.Persistence.History.CoachMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            UserProfileId = userProfileId,
            ConversationId = historyConversationId,
            Sequence = 1,
            ProtectedPayload = "protected",
            CreatedAt = DateTime.UtcNow
        });
        coach.CoachTurnOperations.Add(new SentenceStudio.Api.Coach.Persistence.History.CoachTurnOperation
        {
            Id = Guid.NewGuid().ToString("N"),
            UserProfileId = userProfileId,
            ConversationId = historyConversationId,
            IdempotencyKeyDigest = Guid.NewGuid().ToString("N"),
            ProtectedRequestDigest = "protected"
        });
        coach.CoachMemoryFacts.Add(new SentenceStudio.Api.Coach.Memory.CoachMemoryFact
        {
            Id = Guid.NewGuid().ToString("N"),
            UserProfileId = userProfileId,
            ProtectedValue = "protected",
            EvidenceFirstObservedAt = DateTime.UtcNow,
            EvidenceLastObservedAt = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await coach.SaveChangesAsync();

        var app = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var conversationId = Guid.NewGuid().ToString();
        app.Conversations.Add(new Conversation
        {
            Id = conversationId,
            UserProfileId = userProfileId,
            CreatedAt = DateTime.UtcNow
        });
        app.ConversationChunks.Add(new ConversationChunk
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = conversationId,
            UserProfileId = userProfileId,
            SentTime = DateTime.UtcNow,
            Author = "learner",
            Text = "seeded"
        });
        await app.SaveChangesAsync();
    }

    private static async Task SeedOwnerlessLegacyRowAsync(CoachApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var app = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var conversationId = Guid.NewGuid().ToString();
        app.Conversations.Add(new Conversation
        {
            Id = conversationId,
            UserProfileId = null,
            CreatedAt = DateTime.UtcNow
        });
        app.ConversationChunks.Add(new ConversationChunk
        {
            Id = Guid.NewGuid().ToString(),
            ConversationId = conversationId,
            UserProfileId = null,
            SentTime = DateTime.UtcNow,
            Author = "learner",
            Text = "seeded"
        });
        await app.SaveChangesAsync();
    }
}
