﻿using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SentenceStudio.Data;

#nullable disable

namespace SentenceStudio.Shared.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20250717031426_FixVocabularyProgressConstraints")]
    partial class FixVocabularyProgressConstraints
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder.HasAnnotation("ProductVersion", "9.0.6");

            modelBuilder.Entity("SentenceStudio.Shared.Models.Challenge", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("RecommendedTranslation")
                        .HasColumnType("TEXT");

                    b.Property<string>("SentenceText")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("VocabularyWord")
                        .HasColumnType("TEXT");

                    b.Property<string>("VocabularyWordAsUsed")
                        .HasColumnType("TEXT");

                    b.Property<string>("VocabularyWordGuesses")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("Challenge", (string)null);
                });

            modelBuilder.Entity("SentenceStudio.Shared.Models.Conversation", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("Conversation", (string)null);
                });

            modelBuilder.Entity("SentenceStudio.Shared.Models.ConversationChunk", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("Author")
                        .HasColumnType("TEXT");

                    b.Property<double>("Comprehension")
                        .HasColumnType("REAL");

                    b.Property<string>("ComprehensionNotes")
                        .HasColumnType("TEXT");

                    b.Property<int>("ConversationId")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("SentTime")
                        .HasColumnType("TEXT");

                    b.Property<string>("Text")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("ConversationChunk", (string)null);
                });

            modelBuilder.Entity("SentenceStudio.Shared.Models.GradeResponse", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<double>("Accuracy")
                        .HasColumnType("REAL");

                    b.Property<string>("AccuracyExplanation")
                        .HasColumnType("TEXT");

                    b.Property<int>("ChallengeID")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<double>("Fluency")
                        .HasColumnType("REAL");

                    b.Property<string>("FluencyExplanation")
                        .HasColumnType("TEXT");

                    b.Property<string>("RecommendedTranslation")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("GradeResponse", (string)null);
                });

            modelBuilder.Entity("SentenceStudio.Shared.Models.LearningResource", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("Description")
                        .HasColumnType("TEXT");

                    b.Property<string>("Language")
                        .HasColumnType("TEXT");

                    b.Property<string>("MediaType")
                        .HasColumnType("TEXT");

                    b.Property<string>("MediaUrl")
                        .HasColumnType("TEXT");

                    b.Property<int?>("OldVocabularyListID")
                        .HasColumnType("INTEGER");

                    b.Property<int?>("SkillID")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Tags")
                        .HasColumnType("TEXT");

                    b.Property<string>("Title")
                        .HasColumnType("TEXT");

                    b.Property<string>("Transcript")
                        .HasColumnType("TEXT");

                    b.Property<string>("Translation")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("LearningResource", (string)null);
                });

            modelBuilder.Entity("SentenceStudio.Shared.Models.ResourceVocabularyMapping", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<int>("ResourceId")
                        .HasColumnType("INTEGER");

                    b.Property<int>("VocabularyWordId")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.HasIndex("ResourceId");

                    b.HasIndex("VocabularyWordId");

                    b.ToTable("ResourceVocabularyMapping", (string)null);
                });

            modelBuilder.Entity("SentenceStudio.Shared.Models.SceneImage", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("Description")
                        .HasColumnType("TEXT");

                    b.Property<bool>("IsSelected")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Url")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("SceneImage", (string)null);
                });

            modelBuilder.Entity("SentenceStudio.Shared.Models.SkillProfile", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("Description")
                        .HasColumnType("TEXT");

                    b.Property<string>("Language")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("Title")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("SkillProfile", (string)null);
                });

            modelBuilder.Entity("SentenceStudio.Shared.Models.Story", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("Body")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<int>("ListID")
                        .HasColumnType("INTEGER");

                    b.Property<int>("SkillID")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("Story", (string)null);
                });

            modelBuilder.Entity("SentenceStudio.Shared.Models.StreamHistory", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("AudioFilePath")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<double>("Duration")
                        .HasColumnType("REAL");

                    b.Property<string>("FileName")
                        .HasColumnType("TEXT");

                    b.Property<string>("Phrase")
                        .HasColumnType("TEXT");

                    b.Property<string>("Source")
                        .HasColumnType("TEXT");

                    b.Property<string>("SourceUrl")
                        .HasColumnType("TEXT");

                    b.Property<string>("Title")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("VoiceId")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("StreamHistory", (string)null);
                });

            modelBuilder.Entity("SentenceStudio.Shared.Models.UserActivity", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<double>("Accuracy")
                        .HasColumnType("REAL");

                    b.Property<string>("Activity")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<double>("Fluency")
                        .HasColumnType("REAL");

                    b.Property<string>("Input")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("UserActivity", (string)null);
                });

            modelBuilder.Entity("SentenceStudio.Shared.Models.UserProfile", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("DisplayLanguage")
                        .HasColumnType("TEXT");

                    b.Property<string>("Email")
                        .HasColumnType("TEXT");

                    b.Property<string>("Name")
                        .HasColumnType("TEXT");

                    b.Property<string>("NativeLanguage")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("OpenAI_APIKey")
                        .HasColumnType("TEXT");

                    b.Property<string>("TargetLanguage")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("UserProfile", (string)null);
                });

            modelBuilder.Entity("SentenceStudio.Shared.Models.VocabularyLearningContext", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("Activity")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("ContextType")
                        .HasColumnType("TEXT");

                    b.Property<int>("CorrectAnswersInContext")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<float>("DifficultyScore")
                        .HasColumnType("REAL");

                    b.Property<string>("ExpectedAnswer")
                        .HasColumnType("TEXT");

                    b.Property<string>("InputMode")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("LearnedAt")
                        .HasColumnType("TEXT");

                    b.Property<int?>("LearningResourceId")
                        .HasColumnType("INTEGER");

                    b.Property<int>("ResponseTimeMs")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.Property<float?>("UserConfidence")
                        .HasColumnType("REAL");

                    b.Property<string>("UserInput")
                        .HasColumnType("TEXT");

                    b.Property<int>("VocabularyProgressId")
                        .HasColumnType("INTEGER");

                    b.Property<bool>("WasCorrect")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.HasIndex("LearningResourceId");

                    b.HasIndex("VocabularyProgressId");

                    b.ToTable("VocabularyLearningContext", (string)null);
                });

            modelBuilder.Entity("SentenceStudio.Shared.Models.VocabularyList", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("Name")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("VocabularyList", (string)null);
                });

            modelBuilder.Entity("SentenceStudio.Shared.Models.VocabularyProgress", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<int>("ApplicationAttempts")
                        .HasColumnType("INTEGER");

                    b.Property<int>("ApplicationCorrect")
                        .HasColumnType("INTEGER");

                    b.Property<int>("CorrectAttempts")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<int>("CurrentPhase")
                        .HasColumnType("INTEGER");

                    b.Property<float>("EaseFactor")
                        .HasColumnType("REAL");

                    b.Property<DateTime>("FirstSeenAt")
                        .HasColumnType("TEXT");

                    b.Property<bool>("IsCompleted")
                        .HasColumnType("INTEGER");

                    b.Property<bool>("IsPromoted")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("LastPracticedAt")
                        .HasColumnType("TEXT");

                    b.Property<DateTime?>("MasteredAt")
                        .HasColumnType("TEXT");

                    b.Property<float>("MasteryScore")
                        .HasColumnType("REAL");

                    b.Property<int>("MultipleChoiceCorrect")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime?>("NextReviewDate")
                        .HasColumnType("TEXT");

                    b.Property<int>("ProductionAttempts")
                        .HasColumnType("INTEGER");

                    b.Property<int>("ProductionCorrect")
                        .HasColumnType("INTEGER");

                    b.Property<int>("RecognitionAttempts")
                        .HasColumnType("INTEGER");

                    b.Property<int>("RecognitionCorrect")
                        .HasColumnType("INTEGER");

                    b.Property<int>("ReviewInterval")
                        .HasColumnType("INTEGER");

                    b.Property<int>("TextEntryCorrect")
                        .HasColumnType("INTEGER");

                    b.Property<int>("TotalAttempts")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.Property<int>("UserId")
                        .HasColumnType("INTEGER");

                    b.Property<int>("VocabularyWordId")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.HasIndex("VocabularyWordId")
                        .IsUnique();

                    b.HasIndex("VocabularyWordId", "UserId")
                        .IsUnique();

                    b.ToTable("VocabularyProgress", (string)null);
                });

            modelBuilder.Entity("SentenceStudio.Shared.Models.VocabularyWord", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("NativeLanguageTerm")
                        .HasColumnType("TEXT");

                    b.Property<string>("TargetLanguageTerm")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("VocabularyWord", (string)null);
                });

            modelBuilder.Entity("SentenceStudio.Shared.Models.ResourceVocabularyMapping", b =>
                {
                    b.HasOne("SentenceStudio.Shared.Models.LearningResource", "Resource")
                        .WithMany("VocabularyMappings")
                        .HasForeignKey("ResourceId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("SentenceStudio.Shared.Models.VocabularyWord", "VocabularyWord")
                        .WithMany("ResourceMappings")
                        .HasForeignKey("VocabularyWordId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Resource");

                    b.Navigation("VocabularyWord");
                });

            modelBuilder.Entity("SentenceStudio.Shared.Models.VocabularyLearningContext", b =>
                {
                    b.HasOne("SentenceStudio.Shared.Models.LearningResource", "LearningResource")
                        .WithMany()
                        .HasForeignKey("LearningResourceId")
                        .OnDelete(DeleteBehavior.SetNull);

                    b.HasOne("SentenceStudio.Shared.Models.VocabularyProgress", "VocabularyProgress")
                        .WithMany("LearningContexts")
                        .HasForeignKey("VocabularyProgressId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("LearningResource");

                    b.Navigation("VocabularyProgress");
                });

            modelBuilder.Entity("SentenceStudio.Shared.Models.VocabularyProgress", b =>
                {
                    b.HasOne("SentenceStudio.Shared.Models.VocabularyWord", "VocabularyWord")
                        .WithOne()
                        .HasForeignKey("SentenceStudio.Shared.Models.VocabularyProgress", "VocabularyWordId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("VocabularyWord");
                });

            modelBuilder.Entity("SentenceStudio.Shared.Models.LearningResource", b =>
                {
                    b.Navigation("VocabularyMappings");
                });

            modelBuilder.Entity("SentenceStudio.Shared.Models.VocabularyProgress", b =>
                {
                    b.Navigation("LearningContexts");
                });

            modelBuilder.Entity("SentenceStudio.Shared.Models.VocabularyWord", b =>
                {
                    b.Navigation("ResourceMappings");
                });
#pragma warning restore 612, 618
        }
    }
}
