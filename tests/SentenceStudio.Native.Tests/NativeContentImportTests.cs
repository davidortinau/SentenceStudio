using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Native.Adapters;
using SentenceStudio.Native.Features.Import;
using SentenceStudio.Services;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Import Content (source ImportContent.razor) through the shared ContentImportService on the
/// owner's device database: the wizard's states, what a commit writes, and that an absent or
/// another owner gets nothing and writes nothing.
/// </summary>
public sealed class NativeContentImportTests
{
    private const string A = NativeSliceFixture.UserA;
    private const string B = NativeSliceFixture.UserB;

    [Fact]
    public async Task CsvImport_CreatesTheOwnersResourceWordsAndMappings_AndTheOtherOwnerSeesNothing()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        await fixture.SeedProfileAsync(B);
        var ai = new ScratchImportAi();
        fixture.Activate(A);
        using var import = Coordinator(fixture, Adapter(fixture, ai));
        Assert.True(await import.OpenAsync(A));

        import.SetRawText("사과,apple\n바나나,banana");
        await import.PreviewAsync();
        var preview = import.State;
        Assert.Equal(ImportContentStep.Preview, preview.Step);
        Assert.Equal("Comma-delimited (CSV)", preview.DetectedFormat);
        Assert.Equal(["사과", "바나나"], preview.Rows.Select(row => row.Target));
        Assert.All(preview.Rows, row => Assert.False(row.Duplicate));
        Assert.True(preview.CanCommit);
        Assert.True(preview.SourceLocked);
        // Vocabulary defaults: words only.
        Assert.True(preview.HarvestWords);
        Assert.False(preview.HarvestTranscript || preview.HarvestPhrases || preview.HarvestSentences);

        import.SetNewResourceTitle("Fruit");
        await import.CommitAsync();
        var done = import.State;
        Assert.Equal(ImportContentStep.Complete, done.Step);
        Assert.NotNull(done.Result);
        Assert.Equal(2, done.Result!.CreatedCount);
        Assert.Contains(done.Messages, message => message.Key == "Import_ImportSuccess");
        Assert.Empty(ai.Calls); // both columns were present: no AI translation

        using (var scope = fixture.Scope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var resource = await db.LearningResources.AsNoTracking()
                .SingleAsync(row => row.Id == done.Result.ResourceId);
            Assert.Equal(A, resource.UserProfileId);
            Assert.Equal("Fruit", resource.Title);
            Assert.Equal("Vocabulary List", resource.MediaType);
            Assert.Equal("imported", resource.Tags);
            Assert.Equal("Korean", resource.Language);
            var wordIds = await db.ResourceVocabularyMappings.AsNoTracking()
                .Where(mapping => mapping.ResourceId == resource.Id)
                .Select(mapping => mapping.VocabularyWordId)
                .ToListAsync();
            Assert.Equal(2, wordIds.Count);
            var words = await db.VocabularyWords.AsNoTracking()
                .Where(word => wordIds.Contains(word.Id)).ToListAsync();
            Assert.All(words, word => Assert.Equal(A, word.UserProfileId));
            Assert.Equal(["apple", "banana"], words.Select(word => word.NativeLanguageTerm).Order());
        }
        Assert.True(fixture.Provider.GetRequiredService<NativePendingWrites>().IsPending(A));
        Assert.False(fixture.Provider.GetRequiredService<NativePendingWrites>().IsPending(B));

        // The other owner: A's resource is not offered, and A's words are not B's duplicates.
        fixture.Activate(B);
        var adapter = Adapter(fixture, ai);
        Assert.DoesNotContain(await adapter.ListTargetResourcesAsync(B, default),
            resource => resource.Id == done.Result.ResourceId);
        using var other = Coordinator(fixture, adapter);
        Assert.True(await other.OpenAsync(B));
        Assert.Empty(other.State.Resources);
        other.SetRawText("사과,apple");
        await other.PreviewAsync();
        Assert.False(Assert.Single(other.State.Rows).Duplicate);
    }

    [Fact]
    public async Task Preview_FlagsOnlyTheOwnersExistingWordsAndRepeatsInTheBatch()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(A, wordCount: 1);
        await fixture.SeedAsync(B, wordCount: 1);
        fixture.Activate(A);
        using var import = Coordinator(fixture, Adapter(fixture, new ScratchImportAi()));
        Assert.True(await import.OpenAsync(A));
        // The owner's editable resources are offered for "Add to existing" (source LoadAvailableResources).
        Assert.Equal([fixture.ResourceId(A)], import.State.Resources.Select(resource => resource.Id));

        import.SetRawText($"target-{A}-0,mine\ntarget-{B}-0,theirs\nnew,fresh\nnew,again");
        await import.PreviewAsync();
        var rows = import.State.Rows;
        Assert.Equal(4, rows.Length);
        Assert.True(rows[0].Duplicate);
        Assert.Equal("AlreadyInVocabulary", rows[0].DuplicateReason);
        Assert.False(rows[1].Duplicate);
        Assert.False(rows[2].Duplicate);
        Assert.True(rows[3].Duplicate);
        Assert.Equal("DuplicateWithinBatch", rows[3].DuplicateReason);
    }

    [Fact]
    public async Task AddToExisting_AppendsOnlyToTheOwnersResource_AndSkipsTheExistingWord()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(A, wordCount: 1);
        await fixture.SeedAsync(B, wordCount: 1);
        fixture.Activate(A);
        var adapter = Adapter(fixture, new ScratchImportAi());

        // Another owner's resource id is refused by the shared service (it scopes to the owner).
        using (var foreign = Coordinator(fixture, adapter))
        {
            Assert.True(await foreign.OpenAsync(A));
            foreign.SetRawText("하나,one");
            await foreign.PreviewAsync();
            foreign.SetTargetMode(ImportTargetMode.Existing);
            foreign.SetSelectedResource(fixture.ResourceId(B));
            Assert.Equal(string.Empty, foreign.State.SelectedResourceId);
            await foreign.CommitAsync();
            Assert.Contains(foreign.State.Messages, message => message.Key == "Import_SelectResourceRequired");
            Assert.Null(foreign.State.Result);
        }

        using var import = Coordinator(fixture, adapter);
        Assert.True(await import.OpenAsync(A));
        import.SetRawText($"target-{A}-0,mine\n둘,two");
        await import.PreviewAsync();
        import.SetTargetMode(ImportTargetMode.Existing);
        import.SetSelectedResource(fixture.ResourceId(A));
        await import.CommitAsync();
        var result = import.State.Result!;
        Assert.Equal(fixture.ResourceId(A), result.ResourceId);
        Assert.Equal(1, result.CreatedCount);
        Assert.Equal(1, result.SkippedCount);

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.Equal(2, await db.ResourceVocabularyMappings.CountAsync(
            mapping => mapping.ResourceId == fixture.ResourceId(A)));
        Assert.Equal(1, await db.ResourceVocabularyMappings.CountAsync(
            mapping => mapping.ResourceId == fixture.ResourceId(B)));
    }

    [Fact]
    public async Task AbsentOrAnotherOwner_ReadsNothingAndWritesNothing()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(A, wordCount: 1);
        await fixture.SeedAsync(B, wordCount: 1);
        var ai = new ScratchImportAi();
        var adapter = Adapter(fixture, ai);
        var request = new ContentImportRequest { RawText = "하나,one" };
        var commit = new ContentImportCommit
        {
            Preview = new ContentImportPreview
            {
                Rows = [new ImportRow { RowNumber = 1, TargetLanguageTerm = "하나", NativeLanguageTerm = "one" }]
            },
            Target = new ImportTarget { Mode = ImportTargetMode.New, NewResourceTitle = "Refused" }
        };

        fixture.LoseOwner();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.ListTargetResourcesAsync(A, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.PreviewAsync(A, request, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.ClassifyAsync(A, "하나", default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.CommitAsync(A, commit, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.CommitAsync(string.Empty, commit, default));
        using (var absent = Coordinator(fixture, adapter))
            Assert.False(await absent.OpenAsync(A));

        fixture.Activate(B);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.ListTargetResourcesAsync(A, default));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => adapter.CommitAsync(A, commit, default));
        using (var foreign = Coordinator(fixture, adapter))
            Assert.False(await foreign.OpenAsync(A));

        Assert.Empty(ai.Calls);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.LearningResources.AnyAsync(resource => resource.Title == "Refused"));
        Assert.Equal(2, await db.LearningResources.CountAsync());
        Assert.False(fixture.Provider.GetRequiredService<NativePendingWrites>().IsPending(A));
    }

    [Fact]
    public async Task OwnerChangeAfterPreview_BlocksTheScreenAndCommitsNothing()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        await fixture.SeedProfileAsync(B);
        fixture.Activate(A);
        using var import = Coordinator(fixture, Adapter(fixture, new ScratchImportAi()));
        Assert.True(await import.OpenAsync(A));
        import.SetRawText("하나,one");
        await import.PreviewAsync();
        import.SetNewResourceTitle("Switched");

        fixture.Activate(B);
        await import.CommitAsync();
        Assert.True(import.State.Blocked);
        Assert.Null(import.State.Result);

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.LearningResources.AnyAsync(resource => resource.Title == "Switched"));
    }

    [Fact]
    public async Task Commit_ValidatesLikeTheSourceBeforeWriting()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        fixture.Activate(A);
        using var import = Coordinator(fixture, Adapter(fixture, new ScratchImportAi()));
        Assert.True(await import.OpenAsync(A));
        import.SetRawText("하나,one");
        await import.PreviewAsync();

        await import.CommitAsync();
        Assert.True(import.State.ShowTitleValidationError);
        Assert.Contains(import.State.Messages, message => message.Key == "Import_ResourceTitleRequired");
        // Typing the title clears its error (source ClearTitleError).
        Assert.True(import.SetNewResourceTitle("Numbers"));
        Assert.False(import.State.ShowTitleValidationError);

        import.SetRowSelected(0, false);
        await import.CommitAsync();
        Assert.Contains(import.State.Messages, message => message.Key == "Import_NoRowsSelected");

        import.SetRowSelected(0, true);
        foreach (var harvest in Enum.GetValues<ImportHarvest>())
            import.SetHarvest(harvest, false);
        await import.CommitAsync();
        Assert.True(import.State.ShowHarvestValidationError);
        Assert.Contains(import.State.Messages, message => message.Key == "Import_HarvestValidationError");

        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Assert.False(await db.LearningResources.AnyAsync());

        // Update mode warns, then imports (source ShowWarning before the commit).
        import.SetHarvest(ImportHarvest.Words, true);
        import.SetDedupMode(DedupMode.Update);
        await import.CommitAsync();
        Assert.Contains(import.State.Messages, message => message.Key == "Import_UpdateModeWarning");
        Assert.Equal(1, import.State.Result!.CreatedCount);
    }

    [Fact]
    public async Task AutoDetect_MediumConfidenceWaitsForConfirmation_ThenPreviewsWithTheTypesHarvest()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        fixture.Activate(A);
        var ai = new ScratchImportAi();
        ai.Json["ContentClassificationAiResponse"] =
            """{"type":"phrases","confidence":0.75,"reasoning":"short lines"}""";
        ai.Json["FreeTextVocabularyExtractionResponse"] =
            """{"vocabulary":[{"targetLanguageTerm":"잘 지내요","nativeLanguageTerm":"I'm doing well","confidence":"high"},{"targetLanguageTerm":"밥","nativeLanguageTerm":"rice","confidence":"medium"}]}""";
        using var import = Coordinator(fixture, Adapter(fixture, ai));
        Assert.True(await import.OpenAsync(A));
        import.SetContentType(ContentType.Auto);
        import.SetRawText("잘 지내요\n밥 먹었어요");

        await import.PreviewAsync();
        var gate = import.State;
        Assert.Equal(ImportContentStep.Harvest, gate.Step);
        Assert.Equal(ImportConfidenceBand.Medium, gate.AutoDetect!.Band);
        Assert.Equal(ContentType.Phrases, gate.AutoDetect.ContentType);
        Assert.False(gate.HarvestStepReady);
        Assert.False(gate.HasPreview);
        Assert.False(gate.IsParsing);
        Assert.All(ai.Calls, call => Assert.Equal(A, call.Owner));

        await import.ConfirmAutoDetectAsync();
        var preview = import.State;
        Assert.Equal(ImportContentStep.Preview, preview.Step);
        Assert.True(preview.AutoDetectConfirmed);
        Assert.True(preview.HarvestPhrases && preview.HarvestWords);
        Assert.Equal(["잘 지내요", "밥"], preview.Rows.Select(row => row.Target));
        Assert.Equal(RowStatus.Warning, preview.Rows[1].Status);
        Assert.All(preview.Rows, row => Assert.True(row.AiTranslated));
        Assert.All(ai.Calls, call => Assert.Equal(A, call.Owner));
    }

    [Fact]
    public async Task AutoDetect_LowConfidenceLetsTheLearnerPick_AndTheConfidenceBandsMatchTheSource()
    {
        Assert.Equal(ImportConfidenceBand.High, ImportContentCoordinator.ClassifyConfidence(0.85f));
        Assert.Equal(ImportConfidenceBand.Medium, ImportContentCoordinator.ClassifyConfidence(0.70f));
        Assert.Equal(ImportConfidenceBand.Low, ImportContentCoordinator.ClassifyConfidence(0.69f));

        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        fixture.Activate(A);
        var ai = new ScratchImportAi();
        ai.Json["ContentClassificationAiResponse"] =
            """{"type":"transcript","confidence":0.4,"reasoning":"unclear"}""";
        using var import = Coordinator(fixture, Adapter(fixture, ai));
        Assert.True(await import.OpenAsync(A));
        import.SetContentType(ContentType.Auto);
        import.SetRawText("하나,one");
        await import.PreviewAsync();
        Assert.Equal(ImportConfidenceBand.Low, import.State.AutoDetect!.Band);
        Assert.Equal(ContentType.Vocabulary, import.State.ResolvedContentType);

        await import.OverrideContentTypeAsync(ContentType.Vocabulary);
        Assert.Equal(ImportContentStep.Preview, import.State.Step);
        Assert.Equal("하나", Assert.Single(import.State.Rows).Target);
    }

    [Fact]
    public async Task SingleColumnRows_AreTranslatedByTheOwnersAi_AndStartNewImportResets()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        fixture.Activate(A);
        var ai = new ScratchImportAi();
        ai.Json["BulkTranslationResponse"] =
            """{"translations":[{"targetLanguageTerm":"하나","nativeLanguageTerm":"one"},{"targetLanguageTerm":"둘","nativeLanguageTerm":"two"}]}""";
        using var import = Coordinator(fixture, Adapter(fixture, ai));
        Assert.True(await import.OpenAsync(A));
        import.SetDelimiter(ImportDelimiters.Comma);
        import.SetRawText("하나\n둘");
        await import.PreviewAsync();
        var rows = import.State.Rows;
        Assert.Equal(["one", "two"], rows.Select(row => row.Native));
        Assert.All(rows, row => Assert.True(row.AiTranslated));
        Assert.Equal((A, "BulkTranslationResponse"), Assert.Single(ai.Calls));

        import.SetNewResourceTitle("Counting");
        await import.CommitAsync();
        Assert.NotNull(import.State.Result);
        import.SetResultFilter(ImportItemStatus.Created);
        Assert.Equal(2, import.State.FilteredItems.Length);
        import.SetResultFilter(ImportItemStatus.Failed);
        Assert.Empty(import.State.FilteredItems);

        import.StartNewImport();
        var fresh = import.State;
        Assert.Equal(ImportContentStep.Source, fresh.Step);
        Assert.Null(fresh.Result);
        Assert.Equal(string.Empty, fresh.RawText);
        Assert.Equal(ImportDelimiters.Auto, fresh.Delimiter);
        Assert.Empty(fresh.Rows);
        Assert.True(fresh.HarvestWords);
    }

    [Fact]
    public async Task OwnerChangeDuringCommit_ShowsNoResultToTheNewOwner_AndBlocksTheScreen()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        await fixture.SeedProfileAsync(B);
        fixture.Activate(A);
        var import = new SwitchingImport(Adapter(fixture, new ScratchImportAi()),
            afterCommit: () => fixture.Activate(B));
        using var screen = Coordinator(fixture, import);
        var written = 0;
        screen.ResourcesWritten += () => written++;
        Assert.True(await screen.OpenAsync(A));
        screen.SetRawText("하나,one");
        await screen.PreviewAsync();
        screen.SetNewResourceTitle("Switched during commit");

        await screen.CommitAsync();

        var state = screen.State;
        Assert.True(state.Blocked);
        Assert.Null(state.Result);
        Assert.False(state.CanCommit);
        Assert.Equal(0, written);
        // The rows were committed: nothing reports the import as refused or failed.
        Assert.DoesNotContain(state.Messages, message =>
            message.Key is "Import_ProfileNotFound" or "Import_CommitError");
        // The rows were written for A (the owner the commit ran for), never for B.
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var resource = await db.LearningResources.AsNoTracking()
            .SingleAsync(row => row.Title == "Switched during commit");
        Assert.Equal(A, resource.UserProfileId);
    }

    [Fact]
    public async Task SuccessfulCommit_SignalsResourcesWritten_AndUnsavedWorkFollowsTheDraft()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        fixture.Activate(A);
        using var screen = Coordinator(fixture, Adapter(fixture, new ScratchImportAi()));
        var written = 0;
        screen.ResourcesWritten += () => written++;
        Assert.True(await screen.OpenAsync(A));
        Assert.False(screen.HasUnsavedWork);
        screen.SetRawText("하나,one");
        Assert.True(screen.HasUnsavedWork);
        await screen.PreviewAsync();
        var rowsVersion = screen.RowsVersion;
        // A redraw for another part of the page keeps the rows' version.
        screen.SetDedupMode(DedupMode.ImportAll);
        screen.SetTargetMode(ImportTargetMode.New);
        Assert.Equal(rowsVersion, screen.RowsVersion);
        screen.SetSelectAll(false);
        Assert.NotEqual(rowsVersion, screen.RowsVersion);
        screen.SetSelectAll(true);
        screen.SetNewResourceTitle("Signal");
        await screen.CommitAsync();
        Assert.NotNull(screen.State.Result);
        Assert.Equal(1, written);
        Assert.False(screen.HasUnsavedWork);
    }

    [Fact]
    public async Task ResourceListFailure_IsShownWithRetry_NotAnEmptyPicker()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(A, wordCount: 1);
        fixture.Activate(A);
        var import = new SwitchingImport(Adapter(fixture, new ScratchImportAi())) { FailListOnce = true };
        using var screen = Coordinator(fixture, import);
        Assert.True(await screen.OpenAsync(A));
        Assert.Equal("NativeResources_LoadError", screen.State.ResourcesErrorKey);
        Assert.False(screen.State.ResourcesLoading);
        Assert.Empty(screen.State.Resources);

        await screen.RetryResourcesAsync();
        Assert.Null(screen.State.ResourcesErrorKey);
        Assert.Equal([fixture.ResourceId(A)], screen.State.Resources.Select(resource => resource.Id));
    }

    [Fact]
    public void ServiceTexts_MapToResourceKeys_AndUnknownTextStaysAsIs()
    {
        static string Text(string key) => key switch
        {
            "Import_Warn_AiWordExtractionFailed" => "W[{0}]",
            "Import_Warn_TooLargePhrases" => "P[{0}]",
            "Import_RowError_AiExtractionFailed" => "R[{0}]",
            "Import_Warn_AiExtractionFailed" => "E[{0}]",
            "Import_RowNote_Confidence" => "C[{0}]",
            _ => $"<{key}>"
        };
        Assert.Equal("<Import_Format_Csv>", ImportServiceText.DetectedFormat("Comma-delimited (CSV)", Text));
        Assert.Equal("<Import_ContentType_Transcript>", ImportServiceText.DetectedFormat("Transcript", Text));
        Assert.Equal("<Import_Format_Json>", ImportServiceText.DetectedFormat("JSON", Text));
        Assert.Equal("<Import_Format_Unknown>", ImportServiceText.DetectedFormat("Unknown", Text));
        Assert.Equal("<Import_Reason_AlreadyExists>", ImportServiceText.Message("Already exists in resource", Text));
        Assert.Equal("<Import_PreviewDuplicate_DuplicateWithinBatch>",
            ImportServiceText.Message("Duplicate within batch", Text));
        Assert.Equal("<Import_RowError_NativeMissing>",
            ImportServiceText.Message("Native language term missing (will use AI translation).", Text));
        Assert.Equal("W[timeout]", ImportServiceText.Message(
            "AI word extraction failed: timeout. Primary entries are still available.", Text));
        Assert.Equal("P[60000]", ImportServiceText.Message(
            "Content is too large for phrase extraction (60000 chars). Please split into smaller chunks.", Text));
        Assert.Equal("R[boom]", ImportServiceText.Message(
            "AI extraction failed: boom. Please check your content or provide a structured format.", Text));
        Assert.Equal("E[boom]", ImportServiceText.Message("AI extraction failed: boom", Text));
        Assert.Equal("C[low - unsure]", ImportServiceText.Message("Confidence: low - unsure", Text));
        Assert.Equal("Something new", ImportServiceText.Message("Something new", Text));
        Assert.Equal(string.Empty, ImportServiceText.Message(null, Text));
    }

    [Fact]
    public async Task EveryServiceTextThePageCanShow_MapsToAResourceKey()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(A, wordCount: 1);
        fixture.Activate(A);
        static string Text(string key) => $"<{key}>";
        var shown = new List<(string Raw, string Mapped)>();

        // Each preview path: its detected format, warnings and row errors (what the page shows).
        async Task Preview(ContentImportRequest request, Action<ScratchImportAi>? arrange = null)
        {
            var ai = new ScratchImportAi();
            arrange?.Invoke(ai);
            var preview = await Adapter(fixture, ai).PreviewAsync(A, request, default);
            shown.Add((preview.DetectedFormat, ImportServiceText.DetectedFormat(preview.DetectedFormat, Text)));
            foreach (var text in preview.Warnings.Concat(preview.Rows.Select(row => row.Error)).OfType<string>())
                shown.Add((text, ImportServiceText.Message(text, Text)));
        }

        var tooLarge = new string('가', 50_001);
        const string phrases = "잘 지내요\n밥 먹었어요";
        const string medium =
            """{"vocabulary":[{"targetLanguageTerm":"밥","nativeLanguageTerm":"rice","confidence":"medium","notes":"food"}]}""";
        // Vocabulary: each delimited format, a missing target, a missing native term.
        await Preview(new() { RawText = "사과,apple\n,blank\n바나나,", DelimiterOverride = ',' });
        await Preview(new() { RawText = "하나\tone\n둘\ttwo" });
        await Preview(new() { RawText = "하나|one\n둘|two" });
        await Preview(new() { RawText = """[{"target":"하나","native":"one"},{"native":"no target"}]""" });
        // Single column: a term the AI cannot translate, and a failed translation.
        await Preview(new() { RawText = "하나\n둘", DelimiterOverride = ',' }, ai => ai.Json["BulkTranslationResponse"] =
            """{"translations":[{"targetLanguageTerm":"하나","nativeLanguageTerm":"[unknown]"}]}""");
        await Preview(new() { RawText = "하나\n둘", DelimiterOverride = ',' },
            ai => ai.Fails.Add("BulkTranslationResponse"));
        // Free text: nothing extracted, a medium-confidence entry, a failed AI, too large.
        await Preview(new() { RawText = "그냥 문장입니다" });
        await Preview(new() { RawText = "그냥 문장입니다" },
            ai => ai.Json["FreeTextVocabularyExtractionResponse"] = medium);
        await Preview(new() { RawText = "그냥 문장입니다" },
            ai => ai.Fails.Add("FreeTextVocabularyExtractionResponse"));
        await Preview(new() { RawText = tooLarge });
        // Phrases and sentences: a medium-confidence entry, a failed AI, too large.
        await Preview(new() { RawText = phrases, ContentType = ContentType.Phrases },
            ai => ai.Json["FreeTextVocabularyExtractionResponse"] = medium);
        await Preview(new() { RawText = phrases, ContentType = ContentType.Phrases },
            ai => ai.Fails.Add("FreeTextVocabularyExtractionResponse"));
        await Preview(new() { RawText = tooLarge, ContentType = ContentType.Phrases });
        await Preview(new() { RawText = phrases, ContentType = ContentType.Sentences, HarvestSentences = true });
        // Transcript: stored without extraction, and nothing extracted.
        await Preview(new()
        {
            RawText = phrases, ContentType = ContentType.Transcript, HarvestTranscript = true, HarvestWords = false
        });
        await Preview(new() { RawText = phrases, ContentType = ContentType.Transcript, HarvestTranscript = true });

        // The commit's result reasons: empty target, empty native, existing word, repeat in the batch.
        var result = (await Adapter(fixture, new ScratchImportAi()).CommitAsync(A, new ContentImportCommit
        {
            Preview = new ContentImportPreview
            {
                Rows =
                [
                    new ImportRow { RowNumber = 1, TargetLanguageTerm = "", NativeLanguageTerm = "blank" },
                    new ImportRow { RowNumber = 2, TargetLanguageTerm = "셋", NativeLanguageTerm = "" },
                    new ImportRow { RowNumber = 3, TargetLanguageTerm = $"target-{A}-0", NativeLanguageTerm = "mine" },
                    new ImportRow { RowNumber = 4, TargetLanguageTerm = "넷", NativeLanguageTerm = "four" },
                    new ImportRow { RowNumber = 5, TargetLanguageTerm = "넷", NativeLanguageTerm = "four" }
                ]
            },
            Target = new ImportTarget { Mode = ImportTargetMode.Existing, ExistingResourceId = fixture.ResourceId(A) }
        }, default)).Result!;
        foreach (var reason in result.Items.Select(item => item.Reason).OfType<string>())
            shown.Add((reason, ImportServiceText.Message(reason, Text)));

        // Everything the service produced above maps, and every path above ran: each key was
        // produced at least once by the service itself.
        Assert.All(shown, text => Assert.True(text.Mapped.StartsWith("<Import_", StringComparison.Ordinal),
            $"The page would show the service's English as is: \"{text.Raw}\""));
        var keys = shown.Select(text => text.Mapped).ToHashSet(StringComparer.Ordinal);
        string[] produced =
        [
            "Import_Format_Csv", "Import_Format_Tsv", "Import_Format_Pipe", "Import_Format_Json",
            "Import_Format_FreeText", "Import_Format_Phrases", "Import_Format_Sentences",
            "Import_ContentType_Transcript",
            "Import_Warn_TranscriptOnly", "Import_Warn_TranscriptNoVocab", "Import_Warn_AiWordExtractionFailed",
            "Import_Warn_AiTranslationFailed", "Import_Warn_TooLargePhrases", "Import_Warn_TooLargeFreeText",
            "Import_Warn_AiExtractionFailed",
            "Import_RowError_TargetRequired", "Import_RowError_NativeMissing", "Import_RowError_NotTranslated",
            "Import_RowError_TooLargePhrases", "Import_RowError_TooLargeFreeText", "Import_RowError_NoVocabulary",
            "Import_RowError_AiExtractionFailed", "Import_RowNote_Confidence",
            "Import_Reason_AlreadyExists", "Import_PreviewDuplicate_DuplicateWithinBatch",
            "Import_Reason_TargetEmpty", "Import_Reason_NativeEmpty",
        ];
        Assert.All(produced, key => Assert.Contains($"<{key}>", keys));

        // Not produced here (the service's own text, copied): the unknown format (the service
        // throws before it) and a phrase-linked word in Update mode (needs another learner's
        // phrase graph). They are mapped too, but this test does not run their paths.
        Assert.Equal("<Import_Format_Unknown>", ImportServiceText.DetectedFormat("Unknown", Text));
        Assert.Equal("<Import_Reason_PhraseLinked>",
            ImportServiceText.Message("Phrase-linked entries require manual edit", Text));
    }

    [Fact]
    public async Task Commit_ShowsTheResultBeforeListenersRun_AndOffersTheNewResourceToTheNextImport()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        fixture.Activate(A);
        using var screen = Coordinator(fixture, Adapter(fixture, new ScratchImportAi()));
        ImportContentState? seen = null;
        screen.ResourcesWritten += () =>
        {
            seen = screen.State;
            throw new InvalidOperationException("Simulated listener failure.");
        };
        Assert.True(await screen.OpenAsync(A));
        Assert.Empty(screen.State.Resources);
        screen.SetRawText("하나,one");
        await screen.PreviewAsync();
        screen.SetNewResourceTitle("Listener");

        await screen.CommitAsync();

        // The listener saw the committed state; its failure does not report the import as failed.
        Assert.Equal(ImportContentStep.Complete, seen!.Step);
        Assert.NotNull(seen.Result);
        Assert.False(seen.IsCommitting);
        var done = screen.State;
        Assert.Equal(ImportContentStep.Complete, done.Step);
        Assert.False(done.CanCommit);
        Assert.Contains(done.Messages, message => message.Key == "Import_ImportSuccess");
        Assert.DoesNotContain(done.Messages, message => message.Key == "Import_CommitError");
        var created = done.Result!.ResourceId;
        Assert.Equal([created], done.Resources.Select(resource => resource.Id));
        Assert.False(done.ResourcesLoading);

        // Import more, then Add to existing: the resource just created is offered.
        screen.StartNewImport();
        screen.SetRawText("둘,two");
        await screen.PreviewAsync();
        screen.SetTargetMode(ImportTargetMode.Existing);
        screen.SetSelectedResource(created);
        Assert.Equal(created, screen.State.SelectedResourceId);
        await screen.CommitAsync();
        Assert.Equal(created, screen.State.Result!.ResourceId);
        Assert.Equal(1, screen.State.Result.CreatedCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerChangeInsideTheAdapterCommit_KeepsTheRowsForTheOwner_AndReturnsNothingToShow(
        bool pointerOnly)
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        await fixture.SeedProfileAsync(B);
        fixture.Activate(A);
        var adapter = Adapter(fixture, new ScratchImportAi());
        // The shared service triggers sync after its transaction: the device owner (or only the
        // active-profile pointer) changes there.
        fixture.AmbientSync.OnTrigger = pointerOnly
            ? () => fixture.Preferences.Set(UserProfileRepository.ActiveProfileIdKey, B)
            : () => fixture.Activate(B);
        var committed = await adapter.CommitAsync(A, new ContentImportCommit
        {
            Preview = new ContentImportPreview
            {
                Rows = [new ImportRow { RowNumber = 1, TargetLanguageTerm = "하나", NativeLanguageTerm = "one" }]
            },
            Target = new ImportTarget { Mode = ImportTargetMode.New, NewResourceTitle = "Flipped" }
        }, default);
        fixture.AmbientSync.OnTrigger = null;

        // Committed, not refused, but nothing for whoever owns the device now (fail closed).
        Assert.Null(committed.Result);
        using var scope = fixture.Scope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var resource = await db.LearningResources.AsNoTracking().SingleAsync(row => row.Title == "Flipped");
        Assert.Equal(A, resource.UserProfileId);
        Assert.False(await db.VocabularyWords.AnyAsync(word => word.UserProfileId == B));
        Assert.True(fixture.Provider.GetRequiredService<NativePendingWrites>().IsPending(A));
        Assert.False(fixture.Provider.GetRequiredService<NativePendingWrites>().IsPending(B));
    }

    [Fact]
    public async Task OwnerChangeAfterTheResultIsShown_BlocksAndEndsTheListRead()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        await fixture.SeedProfileAsync(B);
        fixture.Activate(A);
        using var screen = Coordinator(fixture, Adapter(fixture, new ScratchImportAi()));
        // A listener switches the device owner before the list is read again.
        screen.ResourcesWritten += () => fixture.Activate(B);
        Assert.True(await screen.OpenAsync(A));
        screen.SetRawText("하나,one");
        await screen.PreviewAsync();
        screen.SetNewResourceTitle("Shown then switched");
        await screen.CommitAsync();

        var state = screen.State;
        Assert.True(state.Blocked);
        Assert.False(state.ResourcesLoading);
        Assert.False(state.CanCommit);
    }

    [Fact]
    public async Task AnOlderListRead_CannotReplaceANewerOne()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedProfileAsync(A);
        fixture.Activate(A);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var import = new SwitchingImport(Adapter(fixture, new ScratchImportAi())) { HoldFirstList = gate };
        using var screen = Coordinator(fixture, import);
        // The opening read takes its rows (none yet) and is held; the learner imports meanwhile.
        var opening = screen.OpenAsync(A);
        Assert.True(screen.State.ResourcesLoading);
        screen.SetRawText("하나,one");
        await screen.PreviewAsync();
        screen.SetNewResourceTitle("Newer read");
        await screen.CommitAsync();
        var created = screen.State.Result!.ResourceId;
        Assert.Equal([created], screen.State.Resources.Select(resource => resource.Id));

        gate.SetResult();
        Assert.True(await opening);
        Assert.Equal([created], screen.State.Resources.Select(resource => resource.Id));
        Assert.False(screen.State.ResourcesLoading);
    }

    private static NativeContentImport Adapter(NativeSliceFixture fixture, ScratchImportAi ai) =>
        new(fixture.Provider, fixture.UserScope, fixture.Preferences,
            fixture.Provider.GetRequiredService<NativePendingWrites>(),
            fixture.Provider.GetRequiredService<ILoggerFactory>(), ai);

    private static ImportContentCoordinator Coordinator(NativeSliceFixture fixture, INativeContentImport import) =>
        new(import, fixture.UserScope, NullLogger<ImportContentCoordinator>.Instance, key => key);

    /// <summary>The real adapter, with an owner switch after a commit and a failing first list read.</summary>
    private sealed class SwitchingImport(INativeContentImport inner, Action? afterCommit = null)
        : INativeContentImport
    {
        public bool FailListOnce { get; set; }

        /// <summary>The first list read takes its rows, then waits for this before returning them.</summary>
        public TaskCompletionSource? HoldFirstList { get; set; }

        public Task<ContentClassificationResult> ClassifyAsync(
            string owner, string content, CancellationToken cancellationToken) =>
            inner.ClassifyAsync(owner, content, cancellationToken);

        public Task<ContentImportPreview> PreviewAsync(
            string owner, ContentImportRequest request, CancellationToken cancellationToken) =>
            inner.PreviewAsync(owner, request, cancellationToken);

        public async Task<NativeImportCommitted> CommitAsync(
            string owner, ContentImportCommit commit, CancellationToken cancellationToken)
        {
            // The rows commit for the owner; the device owner changes before the screen applies them.
            var committed = await inner.CommitAsync(owner, commit, cancellationToken);
            afterCommit?.Invoke();
            return committed;
        }

        public async Task<IReadOnlyList<NativeImportTargetResource>> ListTargetResourcesAsync(
            string owner, CancellationToken cancellationToken)
        {
            if (FailListOnce)
            {
                FailListOnce = false;
                throw new InvalidOperationException("Simulated resource list failure.");
            }
            var rows = await inner.ListTargetResourcesAsync(owner, cancellationToken);
            if (HoldFirstList is { } hold)
            {
                HoldFirstList = null;
                await hold.Task;
            }
            return rows;
        }
    }

    /// <summary>Answers each prompt with the JSON stored for its response type (by type name).</summary>
    private sealed class ScratchImportAi : IOwnerBoundNativeAiGateway
    {
        private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

        public Dictionary<string, string> Json { get; } = new(StringComparer.Ordinal);

        public List<(string Owner, string Type)> Calls { get; } = [];

        /// <summary>Response types whose prompt fails (as an unreachable AI would).</summary>
        public HashSet<string> Fails { get; } = new(StringComparer.Ordinal);

        public Task<T?> SendPromptAsync<T>(string owner, string prompt, CancellationToken cancellationToken)
        {
            Calls.Add((owner, typeof(T).Name));
            if (Fails.Contains(typeof(T).Name))
                throw new InvalidOperationException("scratch failure");
            return Task.FromResult(Json.TryGetValue(typeof(T).Name, out var json)
                ? JsonSerializer.Deserialize<T>(json, Options) : default);
        }
    }
}
