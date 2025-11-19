
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
        modelBuilder.Entity<ResourceVocabularyMapping>().ToTable("ResourceVocabularyMapping").HasKey(e => e.Id);
        modelBuilder.Entity<VocabularyProgress>().ToTable("VocabularyProgress").HasKey(e => e.Id);
        modelBuilder.Entity<VocabularyLearningContext>().ToTable("VocabularyLearningContext").HasKey(e => e.Id);
        modelBuilder.Entity<DailyPlanCompletion>().ToTable("DailyPlanCompletion").HasKey(e => e.Id);

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
    public DbSet<ResourceVocabularyMapping> ResourceVocabularyMappings => Set<ResourceVocabularyMapping>();
    public DbSet<VocabularyProgress> VocabularyProgresses => Set<VocabularyProgress>();
    public DbSet<VocabularyLearningContext> VocabularyLearningContexts => Set<VocabularyLearningContext>();
    public DbSet<DailyPlanCompletion> DailyPlanCompletions => Set<DailyPlanCompletion>();

}
