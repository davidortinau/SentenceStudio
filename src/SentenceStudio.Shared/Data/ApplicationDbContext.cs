
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Data;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext() { }

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            optionsBuilder.UseSqlite("Data Source=dummy.db");
        }

        // Suppress PendingModelChangesWarning — we manage schema changes via raw SQL
        // for columns like UserProfileId that aren't covered by EF migrations
        optionsBuilder.ConfigureWarnings(w =>
            w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));

        base.OnConfiguring(optionsBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Configure table names to match CoreSync expectations (singular)
        modelBuilder.Entity<LearningResource>().ToTable("LearningResource").HasKey(e => e.Id);
        modelBuilder.Entity<VocabularyWord>().ToTable("VocabularyWord").HasKey(e => e.Id);
        modelBuilder.Entity<VocabularyList>().ToTable("VocabularyList").HasKey(e => e.Id);
        modelBuilder.Entity<SkillProfile>().ToTable("SkillProfile").HasKey(e => e.Id);
        modelBuilder.Entity<UserProfile>().ToTable("UserProfile").HasKey(e => e.Id);
        modelBuilder.Entity<StreamHistory>().ToTable("StreamHistory").HasKey(e => e.Id);
        modelBuilder.Entity<Challenge>().ToTable("Challenge").HasKey(e => e.Id);
        modelBuilder.Entity<Story>().ToTable("Story").HasKey(e => e.Id);
        modelBuilder.Entity<GradeResponse>().ToTable("GradeResponse").HasKey(e => e.Id);
        modelBuilder.Entity<UserActivity>().ToTable("UserActivity").HasKey(e => e.Id);
        modelBuilder.Entity<SceneImage>().ToTable("SceneImage").HasKey(e => e.Id);
        modelBuilder.Entity<Conversation>().ToTable("Conversation").HasKey(e => e.Id);
        modelBuilder.Entity<ConversationChunk>().ToTable("ConversationChunk").HasKey(e => e.Id);
        modelBuilder.Entity<ConversationScenario>().ToTable("ConversationScenario").HasKey(e => e.Id);
        modelBuilder.Entity<ResourceVocabularyMapping>().ToTable("ResourceVocabularyMapping").HasKey(e => e.Id);
        modelBuilder.Entity<VocabularyProgress>().ToTable("VocabularyProgress").HasKey(e => e.Id);
        modelBuilder.Entity<VocabularyLearningContext>().ToTable("VocabularyLearningContext").HasKey(e => e.Id);
        modelBuilder.Entity<DailyPlanCompletion>().ToTable("DailyPlanCompletion").HasKey(e => e.Id);
        modelBuilder.Entity<ExampleSentence>().ToTable("ExampleSentence").HasKey(e => e.Id);
        modelBuilder.Entity<MinimalPair>().ToTable("MinimalPair").HasKey(e => e.Id);
        modelBuilder.Entity<MinimalPairSession>().ToTable("MinimalPairSession").HasKey(e => e.Id);
        modelBuilder.Entity<MinimalPairAttempt>().ToTable("MinimalPairAttempt").HasKey(e => e.Id);
        modelBuilder.Entity<ConversationMemoryState>().ToTable("ConversationMemoryState").HasKey(e => e.Id);

        // Configure relationships for vocabulary progress tracking
        modelBuilder.Entity<VocabularyProgress>()
            .HasOne(vp => vp.VocabularyWord)
            .WithOne()
            .HasForeignKey<VocabularyProgress>(vp => vp.VocabularyWordId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<VocabularyLearningContext>()
            .HasOne(vlc => vlc.VocabularyProgress)
            .WithMany(vp => vp.LearningContexts)
            .HasForeignKey(vlc => vlc.VocabularyProgressId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<VocabularyLearningContext>()
            .HasOne(vlc => vlc.LearningResource)
            .WithMany()
            .HasForeignKey(vlc => vlc.LearningResourceId)
            .OnDelete(DeleteBehavior.SetNull);

        // Configure Conversation to ConversationChunk relationship
        modelBuilder.Entity<Conversation>()
            .HasMany(c => c.Chunks)
            .WithOne()
            .HasForeignKey(cc => cc.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure Conversation to ConversationScenario relationship
        modelBuilder.Entity<Conversation>()
            .HasOne(c => c.Scenario)
            .WithMany()
            .HasForeignKey(c => c.ScenarioId)
            .OnDelete(DeleteBehavior.SetNull);

        // Configure Conversation to ConversationMemoryState relationship
        modelBuilder.Entity<ConversationMemoryState>()
            .HasOne(cms => cms.Conversation)
            .WithMany()
            .HasForeignKey(cms => cms.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique constraint for one memory state per conversation
        modelBuilder.Entity<ConversationMemoryState>()
            .HasIndex(cms => cms.ConversationId)
            .IsUnique();

        // Create unique constraint to ensure one progress record per vocabulary word per user
        modelBuilder.Entity<VocabularyProgress>()
            .HasIndex(vp => new { vp.VocabularyWordId, vp.UserId })
            .IsUnique();

        // Configure many-to-many relationship between LearningResource and VocabularyWord
        modelBuilder.Entity<LearningResource>()
            .HasMany(lr => lr.Vocabulary)
            .WithMany(vw => vw.LearningResources)
            .UsingEntity<ResourceVocabularyMapping>(
                j => j.HasOne(rvm => rvm.VocabularyWord).WithMany(vw => vw.ResourceMappings),
                j => j.HasOne(rvm => rvm.Resource).WithMany(lr => lr.VocabularyMappings));

        // Configure VocabularyWord to ExampleSentence relationship
        modelBuilder.Entity<VocabularyWord>()
            .HasMany(vw => vw.ExampleSentences)
            .WithOne(es => es.VocabularyWord)
            .HasForeignKey(es => es.VocabularyWordId)
            .OnDelete(DeleteBehavior.Cascade);

        // Configure ExampleSentence to LearningResource relationship
        modelBuilder.Entity<ExampleSentence>()
            .HasOne(es => es.LearningResource)
            .WithMany()
            .HasForeignKey(es => es.LearningResourceId)
            .OnDelete(DeleteBehavior.SetNull);

        // Configure MinimalPair relationships and constraints
        modelBuilder.Entity<MinimalPair>()
            .HasOne(mp => mp.VocabularyWordA)
            .WithMany()
            .HasForeignKey(mp => mp.VocabularyWordAId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MinimalPair>()
            .HasOne(mp => mp.VocabularyWordB)
            .WithMany()
            .HasForeignKey(mp => mp.VocabularyWordBId)
            .OnDelete(DeleteBehavior.Cascade);

        // Unique constraint to prevent duplicate pairs (normalized order)
        modelBuilder.Entity<MinimalPair>()
            .HasIndex(mp => new { mp.UserId, mp.VocabularyWordAId, mp.VocabularyWordBId })
            .IsUnique();

        // Configure MinimalPairAttempt relationships
        modelBuilder.Entity<MinimalPairAttempt>()
            .HasOne(mpa => mpa.Session)
            .WithMany(mps => mps.Attempts)
            .HasForeignKey(mpa => mpa.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MinimalPairAttempt>()
            .HasOne(mpa => mpa.Pair)
            .WithMany()
            .HasForeignKey(mpa => mpa.PairId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MinimalPairAttempt>()
            .HasOne(mpa => mpa.PromptWord)
            .WithMany()
            .HasForeignKey(mpa => mpa.PromptWordId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<MinimalPairAttempt>()
            .HasOne(mpa => mpa.SelectedWord)
            .WithMany()
            .HasForeignKey(mpa => mpa.SelectedWordId)
            .OnDelete(DeleteBehavior.Restrict);

        // Indexes for efficient queries
        modelBuilder.Entity<MinimalPairAttempt>()
            .HasIndex(mpa => new { mpa.PairId, mpa.CreatedAt });

        modelBuilder.Entity<MinimalPairAttempt>()
            .HasIndex(mpa => new { mpa.SessionId, mpa.SequenceNumber });

        // Ignore entities that shouldn't be in the database
        modelBuilder.Ignore<Reply>();
        modelBuilder.Ignore<GrammarNotes>();
        modelBuilder.Ignore<Sentence>();
        modelBuilder.Ignore<SyntacticSentence>();
        modelBuilder.Ignore<ShadowingSentence>();
        modelBuilder.Ignore<Question>();
        modelBuilder.Ignore<Lesson>();

        base.OnModelCreating(modelBuilder);
    }


    public DbSet<LearningResource> LearningResources => Set<LearningResource>();
    public DbSet<VocabularyWord> VocabularyWords => Set<VocabularyWord>();
    public DbSet<VocabularyList> VocabularyLists => Set<VocabularyList>();
    public DbSet<SkillProfile> SkillProfiles => Set<SkillProfile>();
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<StreamHistory> StreamHistories => Set<StreamHistory>();
    public DbSet<Challenge> Challenges => Set<Challenge>();
    public DbSet<Story> Stories => Set<Story>();
    public DbSet<GradeResponse> GradeResponses => Set<GradeResponse>();
    public DbSet<UserActivity> UserActivities => Set<UserActivity>();
    public DbSet<SceneImage> SceneImages => Set<SceneImage>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationChunk> ConversationChunks => Set<ConversationChunk>();
    public DbSet<ConversationScenario> ConversationScenarios => Set<ConversationScenario>();
    public DbSet<ResourceVocabularyMapping> ResourceVocabularyMappings => Set<ResourceVocabularyMapping>();
    public DbSet<VocabularyProgress> VocabularyProgresses => Set<VocabularyProgress>();
    public DbSet<VocabularyLearningContext> VocabularyLearningContexts => Set<VocabularyLearningContext>();
    public DbSet<DailyPlanCompletion> DailyPlanCompletions => Set<DailyPlanCompletion>();
    public DbSet<ExampleSentence> ExampleSentences => Set<ExampleSentence>();
    public DbSet<MinimalPair> MinimalPairs => Set<MinimalPair>();
    public DbSet<MinimalPairSession> MinimalPairSessions => Set<MinimalPairSession>();
    public DbSet<MinimalPairAttempt> MinimalPairAttempts => Set<MinimalPairAttempt>();
    public DbSet<ConversationMemoryState> ConversationMemoryStates => Set<ConversationMemoryState>();

}
