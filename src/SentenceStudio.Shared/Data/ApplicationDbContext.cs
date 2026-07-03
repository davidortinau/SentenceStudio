#if !IOS && !ANDROID && !MACCATALYST && !MACOS
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
#endif
using System;
using Microsoft.EntityFrameworkCore;
using SentenceStudio.Shared.Models;
using SentenceStudio.Shared.Models.Numbers;

namespace SentenceStudio.Data;

#if IOS || ANDROID || MACCATALYST || MACOS
public class ApplicationDbContext : DbContext
#else
public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
#endif
{
    public ApplicationDbContext() { }

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
#if IOS || ANDROID || MACCATALYST || MACOS
            optionsBuilder.UseSqlite("Data Source=dummy.db");
#else
            optionsBuilder.UseNpgsql("Host=localhost;Database=sentencestudio_design;Username=postgres;Password=postgres");
#endif

            // Suppress PendingModelChangesWarning so MigrateAsync() can apply pending migrations
            // without throwing when the compiled model is ahead of the last recorded migration.
            // Only for non-DI contexts (design-time, mobile fallback). DI-configured contexts
            // (Aspire pooled) must configure warnings in AddDbContext/AddNpgsqlDbContext instead.
            optionsBuilder.ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
        }

        base.OnConfiguring(optionsBuilder);
    }

    /// <summary>
    /// Enables WAL journal mode for concurrent read access across multiple processes.
    /// Call once after database creation/migration. SQLite-only.
    /// </summary>
    public void EnableWalMode()
    {
#if IOS || ANDROID || MACCATALYST || MACOS
        Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
#endif
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Configure table names to match CoreSync expectations (singular)
        // Synced entities use string (GUID) PKs — tell EF not to auto-generate values
        modelBuilder.Entity<LearningResource>().ToTable("LearningResource").HasKey(e => e.Id);
        modelBuilder.Entity<LearningResource>().Property(e => e.Id).ValueGeneratedNever();
        
        modelBuilder.Entity<VocabularyWord>().ToTable("VocabularyWord").HasKey(e => e.Id);
        modelBuilder.Entity<VocabularyWord>().Property(e => e.Id).ValueGeneratedNever();
        // Explicit nullable mapping — [ObservableProperty] source generator can strip
        // nullability annotations, causing EF Core to skip IsDBNull checks on read.
        modelBuilder.Entity<VocabularyWord>().Property(e => e.NativeLanguageTerm).IsRequired(false);
        modelBuilder.Entity<VocabularyWord>().Property(e => e.TargetLanguageTerm).IsRequired(false);
        modelBuilder.Entity<VocabularyWord>().Property(e => e.Lemma).IsRequired(false);
        modelBuilder.Entity<VocabularyWord>().Property(e => e.Language).IsRequired(false);
        modelBuilder.Entity<VocabularyWord>().Property(e => e.Tags).IsRequired(false);
        modelBuilder.Entity<VocabularyWord>().Property(e => e.MnemonicText).IsRequired(false);
        modelBuilder.Entity<VocabularyWord>().Property(e => e.MnemonicImageUri).IsRequired(false);
        modelBuilder.Entity<VocabularyWord>().Property(e => e.AudioPronunciationUri).IsRequired(false);
        // LexicalUnitType enum: explicit int conversion + default value (0 = Unknown)
        // Required for reliable 0-storage on backfill across both PostgreSQL (server) and SQLite (MAUI)
        modelBuilder.Entity<VocabularyWord>().Property(e => e.LexicalUnitType).HasConversion<int>().HasDefaultValue(LexicalUnitType.Unknown);
        
        modelBuilder.Entity<VocabularyList>().ToTable("VocabularyList").HasKey(e => e.Id);
        modelBuilder.Entity<VocabularyList>().Property(e => e.Id).ValueGeneratedNever();
        
        modelBuilder.Entity<SkillProfile>().ToTable("SkillProfile").HasKey(e => e.Id);
        modelBuilder.Entity<SkillProfile>().Property(e => e.Id).ValueGeneratedNever();
        
        modelBuilder.Entity<UserProfile>().ToTable("UserProfile").HasKey(e => e.Id);
        modelBuilder.Entity<UserProfile>().Property(e => e.Id).ValueGeneratedNever();
        
        modelBuilder.Entity<Challenge>().ToTable("Challenge").HasKey(e => e.Id);
        modelBuilder.Entity<Challenge>().Property(e => e.Id).ValueGeneratedNever();
        
        modelBuilder.Entity<Conversation>().ToTable("Conversation").HasKey(e => e.Id);
        modelBuilder.Entity<Conversation>().Property(e => e.Id).ValueGeneratedNever();
        
        modelBuilder.Entity<ConversationChunk>().ToTable("ConversationChunk").HasKey(e => e.Id);
        modelBuilder.Entity<ConversationChunk>().Property(e => e.Id).ValueGeneratedNever();
        
        modelBuilder.Entity<ResourceVocabularyMapping>().ToTable("ResourceVocabularyMapping").HasKey(e => e.Id);
        modelBuilder.Entity<ResourceVocabularyMapping>().Property(e => e.Id).ValueGeneratedNever();
        modelBuilder.Entity<ResourceVocabularyMapping>()
            .HasIndex(e => new { e.ResourceId, e.VocabularyWordId });
        modelBuilder.Entity<ResourceVocabularyMapping>()
            .HasIndex(e => e.VocabularyWordId);
        
        modelBuilder.Entity<VocabularyProgress>().ToTable("VocabularyProgress").HasKey(e => e.Id);
        modelBuilder.Entity<VocabularyProgress>().Property(e => e.Id).ValueGeneratedNever();
        modelBuilder.Entity<LearningResource>()
            .HasIndex(e => e.UserProfileId);
        
        modelBuilder.Entity<VocabularyLearningContext>().ToTable("VocabularyLearningContext").HasKey(e => e.Id);
        modelBuilder.Entity<VocabularyLearningContext>().Property(e => e.Id).ValueGeneratedNever();

        // Synced entities (string GUID PKs)
        modelBuilder.Entity<DailyPlanCompletion>().ToTable("DailyPlanCompletion").HasKey(e => e.Id);
        modelBuilder.Entity<DailyPlanCompletion>().Property(e => e.Id).ValueGeneratedNever();
        // §14a: Date is a user-local calendar day, not an instant. On PG we
        // force the column to `date` (TZ-less) so Npgsql's legacy-timestamp
        // behavior cannot shift the value on read in non-UTC server zones.
        // SQLite stores DateTime as ISO8601 TEXT regardless and has no TZ
        // arithmetic, so the legacy bug doesn't apply there — leave its
        // mapping alone to avoid disrupting CoreSync wire format on devices.
        var isNpgsql = Database.ProviderName?.Contains("Npgsql", StringComparison.OrdinalIgnoreCase) == true;
        if (isNpgsql)
        {
            modelBuilder.Entity<DailyPlanCompletion>()
                .Property(e => e.Date)
                .HasColumnType("date");
        }
        // Strict uniqueness: one row per (user, local-day, plan-item). Prevents
        // CoreSync ↔ HTTP duplicate inserts when both write the same logical item.
        modelBuilder.Entity<DailyPlanCompletion>()
            .HasIndex(e => new { e.UserProfileId, e.Date, e.PlanItemId })
            .IsUnique();

        // Parent DailyPlan — owns generation metadata + language-neutral
        // narrative/rationale facts. One row per (user, local-day).
        modelBuilder.Entity<DailyPlan>().ToTable("DailyPlan").HasKey(e => e.Id);
        modelBuilder.Entity<DailyPlan>().Property(e => e.Id).ValueGeneratedNever();
        modelBuilder.Entity<DailyPlan>().Property(e => e.RationaleFacts).IsRequired(false);
        modelBuilder.Entity<DailyPlan>().Property(e => e.NarrativeFacts).IsRequired(false);
        modelBuilder.Entity<DailyPlan>().Property(e => e.FocusVocabularyFacts).IsRequired(false);
        if (isNpgsql)
        {
            // §14a — same rationale as DailyPlanCompletion.Date above.
            modelBuilder.Entity<DailyPlan>()
                .Property(e => e.Date)
                .HasColumnType("date");
        }
        modelBuilder.Entity<DailyPlan>()
            .HasIndex(e => new { e.UserProfileId, e.Date })
            .IsUnique();

        modelBuilder.Entity<UserActivity>().ToTable("UserActivity").HasKey(e => e.Id);
        modelBuilder.Entity<UserActivity>().Property(e => e.Id).ValueGeneratedNever();

        modelBuilder.Entity<DiaryEntry>().ToTable("DiaryEntry").HasKey(e => e.Id);
        modelBuilder.Entity<DiaryEntry>().Property(e => e.Id).ValueGeneratedNever();
        modelBuilder.Entity<DiaryEntry>().Property(e => e.Language).IsRequired(false);
        modelBuilder.Entity<DiaryEntry>().Property(e => e.PromptText).IsRequired(false);
        modelBuilder.Entity<DiaryEntry>().Property(e => e.PromptHint).IsRequired(false);
        modelBuilder.Entity<DiaryEntry>().Property(e => e.FeedbackRecommended).IsRequired(false);
        modelBuilder.Entity<DiaryEntry>().Property(e => e.FeedbackNotes).IsRequired(false);
        modelBuilder.Entity<DiaryEntry>().Property(e => e.FeedbackStrengths).IsRequired(false);
        // One diary entry per (user, day, language). Allows multi-language diaries later
        // without breaking the one-per-day-per-language rule.
        modelBuilder.Entity<DiaryEntry>()
            .HasIndex(e => new { e.UserProfileId, e.EntryDate, e.Language })
            .IsUnique();
        
        // Non-synced entities (keep int auto-increment PKs)
        modelBuilder.Entity<StreamHistory>().ToTable("StreamHistory").HasKey(e => e.Id);
        modelBuilder.Entity<Story>().ToTable("Story").HasKey(e => e.Id);
        modelBuilder.Entity<GradeResponse>().ToTable("GradeResponse").HasKey(e => e.Id);
        modelBuilder.Entity<SceneImage>().ToTable("SceneImage").HasKey(e => e.Id);
        modelBuilder.Entity<ConversationScenario>().ToTable("ConversationScenario").HasKey(e => e.Id);
        modelBuilder.Entity<ExampleSentence>().ToTable("ExampleSentence").HasKey(e => e.Id);
        modelBuilder.Entity<MinimalPair>().ToTable("MinimalPair").HasKey(e => e.Id);
        modelBuilder.Entity<MinimalPairSession>().ToTable("MinimalPairSession").HasKey(e => e.Id);
        modelBuilder.Entity<MinimalPairAttempt>().ToTable("MinimalPairAttempt").HasKey(e => e.Id);
        modelBuilder.Entity<ActivitySession>().ToTable("ActivitySession").HasKey(e => e.Id);
        modelBuilder.Entity<ActivitySession>().Property(e => e.Status).HasConversion<string>();
        modelBuilder.Entity<ActivitySession>().HasIndex(e => new { e.UserId, e.ActivityType, e.Status });
        modelBuilder.Entity<ActivitySession>()
            .HasIndex(e => new { e.UserId, e.ActivityType, e.LaunchContextKey })
            .IsUnique()
            .HasFilter("\"Status\" = 'InProgress'");
        modelBuilder.Entity<ConversationMemoryState>().ToTable("ConversationMemoryState").HasKey(e => e.Id);

        // Configure relationships for vocabulary progress tracking
        modelBuilder.Entity<VocabularyProgress>()
            .HasOne(vp => vp.VocabularyWord)
            .WithMany()
            .HasForeignKey(vp => vp.VocabularyWordId)
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

        // YouTube channel monitoring entities — string GUID PKs, synced
        modelBuilder.Entity<MonitoredChannel>().ToTable("MonitoredChannel").HasKey(e => e.Id);
        modelBuilder.Entity<MonitoredChannel>().Property(e => e.Id).ValueGeneratedNever();

        modelBuilder.Entity<VideoImport>().ToTable("VideoImport").HasKey(e => e.Id);
        modelBuilder.Entity<VideoImport>().Property(e => e.Id).ValueGeneratedNever();

        // MonitoredChannel → VideoImport (one-to-many)
        modelBuilder.Entity<VideoImport>()
            .HasOne(vi => vi.MonitoredChannel)
            .WithMany(mc => mc.VideoImports)
            .HasForeignKey(vi => vi.MonitoredChannelId)
            .OnDelete(DeleteBehavior.SetNull);

        // VideoImport → LearningResource (optional FK)
        modelBuilder.Entity<VideoImport>()
            .HasOne(vi => vi.LearningResource)
            .WithMany()
            .HasForeignKey(vi => vi.LearningResourceId)
            .OnDelete(DeleteBehavior.SetNull);

        // Index for quick lookups: find imports by YouTube video ID
        modelBuilder.Entity<VideoImport>()
            .HasIndex(vi => vi.VideoId);

        // Index for channel polling: find active channels due for check
        modelBuilder.Entity<MonitoredChannel>()
            .HasIndex(mc => new { mc.IsActive, mc.LastCheckedAt });

        // WordAssociationScore — string GUID PK, not synced but uses GUIDs for consistency
        modelBuilder.Entity<WordAssociationScore>().ToTable("WordAssociationScore").HasKey(e => e.Id);
        modelBuilder.Entity<WordAssociationScore>().Property(e => e.Id).ValueGeneratedNever();

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

        // RefreshToken — server-side token storage for JWT refresh flow
        modelBuilder.Entity<RefreshToken>().ToTable("RefreshTokens");
        modelBuilder.Entity<RefreshToken>()
            .HasOne(rt => rt.User)
            .WithMany()
            .HasForeignKey(rt => rt.UserId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<RefreshToken>()
            .HasIndex(rt => rt.Token)
            .IsUnique();

        // PhraseConstituent — join entity for phrase-to-constituent relationships
        modelBuilder.Entity<PhraseConstituent>().ToTable("PhraseConstituent").HasKey(e => e.Id);
        modelBuilder.Entity<PhraseConstituent>().Property(e => e.Id).ValueGeneratedNever();
        
        // Both FKs target VocabularyWord — must disambiguate with explicit HasForeignKey
        modelBuilder.Entity<PhraseConstituent>()
            .HasOne(pc => pc.PhraseWord)
            .WithMany()
            .HasForeignKey(pc => pc.PhraseWordId)
            .OnDelete(DeleteBehavior.Cascade);
        
        modelBuilder.Entity<PhraseConstituent>()
            .HasOne(pc => pc.ConstituentWord)
            .WithMany()
            .HasForeignKey(pc => pc.ConstituentWordId)
            .OnDelete(DeleteBehavior.SetNull);
        
        // Indexes for efficient lookups
        modelBuilder.Entity<PhraseConstituent>()
            .HasIndex(pc => pc.PhraseWordId);
        
        modelBuilder.Entity<PhraseConstituent>()
            .HasIndex(pc => pc.ConstituentWordId);
        
        // Unique constraint: one constituent link per phrase-constituent pair
        modelBuilder.Entity<PhraseConstituent>()
            .HasIndex(pc => new { pc.PhraseWordId, pc.ConstituentWordId })
            .IsUnique();

        // NumberDrill entities configuration (Phase 1 — MVP)
        modelBuilder.Entity<NumberContext>().ToTable("NumberContext").HasKey(e => e.Id);
        modelBuilder.Entity<NumberContext>().Property(e => e.Id).ValueGeneratedNever();
        modelBuilder.Entity<NumberContext>().HasIndex(e => e.Code).IsUnique();
        modelBuilder.Entity<NumberContext>().Property(e => e.DefaultSystem).HasConversion<string>();

        modelBuilder.Entity<NumberCounter>().ToTable("NumberCounter").HasKey(e => e.Id);
        modelBuilder.Entity<NumberCounter>().Property(e => e.Id).ValueGeneratedNever();
        modelBuilder.Entity<NumberCounter>().Property(e => e.System).HasConversion<string>();

        modelBuilder.Entity<NumberSubMode>().ToTable("NumberSubMode").HasKey(e => e.Id);
        modelBuilder.Entity<NumberSubMode>().Property(e => e.Id).ValueGeneratedNever();
        modelBuilder.Entity<NumberSubMode>().HasIndex(e => e.Code).IsUnique();

        modelBuilder.Entity<NumberMasteryProgress>().ToTable("NumberMasteryProgress").HasKey(e => e.Id);
        modelBuilder.Entity<NumberMasteryProgress>().Property(e => e.Id).ValueGeneratedNever();
        modelBuilder.Entity<NumberMasteryProgress>().HasIndex(e => e.UserProfileId);
        modelBuilder.Entity<NumberMasteryProgress>().Property(e => e.System).HasConversion<string>();
        modelBuilder.Entity<NumberMasteryProgress>()
            .HasIndex(e => new { e.UserProfileId, e.LanguageCode, e.ContextCode, e.CounterId, e.System, e.Bucket })
            .IsUnique();

        modelBuilder.Entity<NumberAttempt>().ToTable("NumberAttempt").HasKey(e => e.Id);
        modelBuilder.Entity<NumberAttempt>().Property(e => e.Id).ValueGeneratedNever();
        modelBuilder.Entity<NumberAttempt>().HasIndex(e => e.UserProfileId);
        modelBuilder.Entity<NumberAttempt>().Property(e => e.System).HasConversion<string>();

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
    public DbSet<DiaryEntry> DiaryEntries => Set<DiaryEntry>();
    public DbSet<SceneImage> SceneImages => Set<SceneImage>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationChunk> ConversationChunks => Set<ConversationChunk>();
    public DbSet<ConversationScenario> ConversationScenarios => Set<ConversationScenario>();
    public DbSet<ResourceVocabularyMapping> ResourceVocabularyMappings => Set<ResourceVocabularyMapping>();
    public DbSet<VocabularyProgress> VocabularyProgresses => Set<VocabularyProgress>();
    public DbSet<VocabularyLearningContext> VocabularyLearningContexts => Set<VocabularyLearningContext>();
    public DbSet<DailyPlanCompletion> DailyPlanCompletions => Set<DailyPlanCompletion>();
    public DbSet<DailyPlan> DailyPlans => Set<DailyPlan>();
    public DbSet<ExampleSentence> ExampleSentences => Set<ExampleSentence>();
    public DbSet<MinimalPair> MinimalPairs => Set<MinimalPair>();
    public DbSet<MinimalPairSession> MinimalPairSessions => Set<MinimalPairSession>();
    public DbSet<MinimalPairAttempt> MinimalPairAttempts => Set<MinimalPairAttempt>();
    public DbSet<ActivitySession> ActivitySessions => Set<ActivitySession>();
    public DbSet<ConversationMemoryState> ConversationMemoryStates => Set<ConversationMemoryState>();
    public DbSet<WordAssociationScore> WordAssociationScores => Set<WordAssociationScore>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<MonitoredChannel> MonitoredChannels => Set<MonitoredChannel>();
    public DbSet<VideoImport> VideoImports => Set<VideoImport>();
    public DbSet<PhraseConstituent> PhraseConstituents => Set<PhraseConstituent>();

    // NumberDrill entities (Phase 1 — MVP)
    public DbSet<NumberContext> NumberContexts => Set<NumberContext>();
    public DbSet<NumberCounter> NumberCounters => Set<NumberCounter>();
    public DbSet<NumberSubMode> NumberSubModes => Set<NumberSubMode>();
    public DbSet<NumberMasteryProgress> NumberMasteryProgresses => Set<NumberMasteryProgress>();
    public DbSet<NumberAttempt> NumberAttempts => Set<NumberAttempt>();

}
