using System.Diagnostics;
using CoreSync;
using CoreSync.PostgreSQL;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SentenceStudio.Api.Tests.AppOperations;
using SentenceStudio.Data;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Api.Tests.Sync;

/// <summary>
/// The two one-time server scripts that come with the owner-only word rule (Captain 2026-11-06),
/// run through psql against a real PostgreSQL database with CoreSync's real tracking triggers:
/// <c>scripts/record-shared-vocabulary-words.sql</c> (records to one learner the ownerless words
/// they share only with test accounts and deleted accounts' progress) and
/// <c>scripts/send-withheld-vocabulary-rows.sql</c> (touches the rows the new sync filter sends
/// that the old, resource-based filter withheld, so devices download them). Both are dry run by
/// default, refuse an apply without the dry run's counts, and record one CoreSync change per row.
/// </summary>
public sealed class WithheldVocabularyRowsScriptPostgresTests
{
    private const string Target = "10000000-0000-0000-0000-00000000000a";
    private const string Other = "20000000-0000-0000-0000-00000000000b";
    private const string Tester = "30000000-0000-0000-0000-00000000000c";
    private const string Mixed = "40000000-0000-0000-0000-00000000000d";
    private const string Gone = "50000000-0000-0000-0000-00000000000e";
    private const string Ghost = "60000000-0000-0000-0000-00000000000f";
    private const string TestDomains = "sentencestudio.test";
    private static readonly DateTime At = new(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc);

    private const string RecordScript = "scripts/record-shared-vocabulary-words.sql";
    private const string SendScript = "scripts/send-withheld-vocabulary-rows.sql";

    private static readonly string[] Recorded =
        ["w-test1", "w-test2", "w-phrase-part", "w-np1", "w-both1"];

    private static readonly string[] LeftOwnerless =
    [
        "w-neg-other", "w-neg-ownerless-res", "w-neg-notarget", "w-neg-mixed",
        "w-neg-pair-gone", "w-pair-partner", "w-target-only", "ownerless-in-rt", "w-nobody",
        "w-neg-sentence-x"
    ];

    private static readonly Dictionary<string, string[]> Withheld = new(StringComparer.Ordinal)
    {
        ["VocabularyWord"] = ["o-free", "o-xlinked", "w-phrase-part", "w-test2", "xw-free"],
        ["VocabularyProgress"] = ["p-t-free", "p-t-part", "p-t-test2", "p-x-xfree"],
        ["VocabularyLearningContext"] = ["c-t-free", "c-t-test2"],
        ["ExampleSentence"] = ["es-t-free", "s-rec3"],
        ["PhraseConstituent"] = ["ph-free-link", "ph-free-null", "ph-link-free", "ph-test1-part"],
        ["MinimalPair"] = ["mp-t-free-link"],
        ["MinimalPairAttempt"] = ["ma-t-link-free"],
    };

    [PsqlScriptPostgresFact]
    public async Task RecordShared_RecordsOnlyWordsSharedWithTestAccountsAndGoneAccounts()
    {
        await using var database = await ScriptDatabase.CreateAsync();
        await database.SeedAsync();
        var trackedBefore = await database.MaxTrackedAsync();

        var dry = await database.RunAsync(RecordScript, RecordArgs());
        Assert.True(dry.ExitCode == 0, dry.Text);
        Assert.Contains("Dry run: rolled back. Nothing changed.", dry.Stdout);
        Assert.Equal(new[] { "15", "3", "1", "1", "5", "3", "2" }, dry.ValuesAfter(
            "ownerless words|test|nonprofile|both|words to record|sentences to record|passes"));
        Assert.Equal(15, await database.OwnerlessWordsAsync());
        Assert.Equal(trackedBefore, await database.MaxTrackedAsync());

        var wrong = await database.RunAsync(RecordScript, RecordArgs(
            ("expected_test", "52"), ("expected_nonprofile", "1"), ("expected_both", "1"),
            ("expected_sentences", "3"), ("apply", "true")));
        Assert.NotEqual(0, wrong.ExitCode);
        Assert.Contains("Would record 3 words shared with test accounts, expected 52. Nothing changed.",
            wrong.Stderr);
        Assert.Equal(15, await database.OwnerlessWordsAsync());

        var missing = await database.RunAsync(RecordScript, RecordArgs(("apply", "true")));
        Assert.NotEqual(0, missing.ExitCode);
        Assert.Contains("apply=true needs expected_test", missing.Stderr);

        var testTarget = await database.RunAsync(RecordScript,
            [("target_profile", Tester), ("test_domains", TestDomains)]);
        Assert.NotEqual(0, testTarget.ExitCode);
        Assert.Contains("target_profile has an email in a test domain", testTarget.Stderr);
        Assert.Equal(trackedBefore, await database.MaxTrackedAsync());

        var apply = await database.RunAsync(RecordScript, RecordArgs(
            ("expected_test", "3"), ("expected_nonprofile", "1"), ("expected_both", "1"),
            ("expected_sentences", "3"), ("apply", "true")));
        Assert.True(apply.ExitCode == 0, apply.Text);
        Assert.Contains("Committed.", apply.Stdout);

        await using (var db = database.NewContext())
        {
            var owners = await db.VocabularyWords.AsNoTracking()
                .Where(word => Recorded.Contains(word.Id) || LeftOwnerless.Contains(word.Id))
                .ToDictionaryAsync(word => word.Id, word => word.UserProfileId);
            Assert.All(Recorded, id => Assert.Equal(Target, owners[id]));
            Assert.All(LeftOwnerless, id => Assert.True(string.IsNullOrEmpty(owners[id]), id));
            var sentences = await db.ExampleSentences.AsNoTracking()
                .Where(sentence => sentence.Id.StartsWith("s-"))
                .ToDictionaryAsync(sentence => sentence.Id, sentence => sentence.UserProfileId);
            Assert.Equal(Target, sentences["s-rec1"]);
            Assert.Equal(Target, sentences["s-rec2"]);
            Assert.Equal(Target, sentences["s-rec3"]);
            // In the test account's resource: not the target's to take.
            Assert.Null(sentences["s-keep-k"]);
            Assert.Equal(Other, sentences["s-x"]);
            // Nothing of the other accounts changed.
            Assert.Equal(2, await db.ResourceVocabularyMappings.CountAsync(
                mapping => mapping.ResourceId == "rk" && (mapping.VocabularyWordId == "w-test1"
                    || mapping.VocabularyWordId == "w-both1")));
            Assert.Equal(1, await db.VocabularyProgresses.CountAsync(
                progress => progress.Id == "p-k-test2" && progress.UserId == Tester));
        }

        var tracked = await database.TrackedSinceAsync(trackedBefore);
        Assert.Equal(
            Recorded.Select(id => ("VocabularyWord", id))
                .Concat(new[] { "s-rec1", "s-rec2", "s-rec3" }.Select(id => ("ExampleSentence", id)))
                .OrderBy(row => row).ToArray(),
            tracked.Select(row => (row.Table, row.Id)).OrderBy(row => row).ToArray());
        Assert.All(tracked, row => Assert.Equal(("U", (string?)null), (row.Op, row.Source)));

        var again = await database.RunAsync(RecordScript, RecordArgs());
        Assert.True(again.ExitCode == 0, again.Text);
        Assert.Equal(new[] { "10", "0", "0", "0", "0", "0", "0" }, again.ValuesAfter(
            "ownerless words|test|nonprofile|both|words to record|sentences to record|passes"));
    }

    [PsqlScriptPostgresFact]
    public async Task SendWithheld_TouchesExactlyTheRowsTheOldFilterWithheld_AndARepeatNeedsAFreshDryRun()
    {
        await using var database = await ScriptDatabase.CreateAsync();
        await database.SeedAsync();
        // The runbook order: record the shared words first, so the touch sends them too.
        var record = await database.RunAsync(RecordScript, RecordArgs(
            ("expected_test", "3"), ("expected_nonprofile", "1"), ("expected_both", "1"),
            ("expected_sentences", "3"), ("apply", "true")));
        Assert.True(record.ExitCode == 0, record.Text);
        var updatedAt = await database.UpdatedAtAsync("o-free");

        var dry = await database.RunAsync(SendScript, []);
        Assert.True(dry.ExitCode == 0, dry.Text);
        Assert.Contains("Dry run: rolled back. Nothing changed.", dry.Stdout);
        var totals = dry.ValuesAfter(
            "words|progress|contexts|sentences|phrases|pairs|answers|tracked up to");
        Assert.Equal(new[] { "5", "4", "2", "2", "4", "1", "1" }, totals[..7]);
        var trackedMark = totals[7];
        Assert.Equal(long.Parse(trackedMark), await database.MaxTrackedAsync());

        var wrongCount = await database.RunAsync(SendScript, SendArgs(trackedMark, phrases: "3"));
        Assert.NotEqual(0, wrongCount.ExitCode);
        Assert.Contains("Would send 4 PhraseConstituent rows, expected 3. Nothing changed.",
            wrongCount.Stderr);

        var stale = await database.RunAsync(SendScript,
            SendArgs((long.Parse(trackedMark) - 1).ToString()));
        Assert.NotEqual(0, stale.ExitCode);
        Assert.Contains("Run the dry run again. Nothing changed.", stale.Stderr);

        var missing = await database.RunAsync(SendScript, [("apply", "true")]);
        Assert.NotEqual(0, missing.ExitCode);
        Assert.Contains("apply=true needs every expected_* value", missing.Stderr);
        Assert.Equal(long.Parse(trackedMark), await database.MaxTrackedAsync());

        var apply = await database.RunAsync(SendScript, SendArgs(trackedMark));
        Assert.True(apply.ExitCode == 0, apply.Text);
        Assert.Contains("Committed.", apply.Stdout);

        // Exactly the planned rows, each once, as a server-side update; nothing else in these
        // tables (old-visible rows, other learners' rows, ownerless and non-profile words).
        var tracked = await database.TrackedSinceAsync(long.Parse(trackedMark));
        Assert.Equal(
            Withheld.SelectMany(table => table.Value.Select(id => (table.Key, id)))
                .OrderBy(row => row).ToArray(),
            tracked.Select(row => (row.Table, row.Id)).OrderBy(row => row).ToArray());
        Assert.All(tracked, row => Assert.Equal(("U", (string?)null), (row.Op, row.Source)));
        // A touch, not an edit.
        Assert.Equal(updatedAt, await database.UpdatedAtAsync("o-free"));

        var repeat = await database.RunAsync(SendScript, SendArgs(trackedMark));
        Assert.NotEqual(0, repeat.ExitCode);
        Assert.Contains("Run the dry run again. Nothing changed.", repeat.Stderr);

        // The set depends only on the data, so a fresh dry run finds the same rows.
        var again = await database.RunAsync(SendScript, []);
        Assert.True(again.ExitCode == 0, again.Text);
        Assert.Equal(new[] { "5", "4", "2", "2", "4", "1", "1" }, again.ValuesAfter(
            "words|progress|contexts|sentences|phrases|pairs|answers|tracked up to")[..7]);
    }

    // Review finding 12: the owner backfill's phrase passes stop at a limit. Reaching it with words
    // still resolving is reported, the words it did not reach get that reason (not "its one
    // learner is not a profile"), and the apply is refused; a higher limit records them all.
    [PsqlScriptPostgresFact]
    public async Task AssignOwners_ReportsItsPassLimit_AndRefusesToApplyWhenItWasReached()
    {
        await using var database = await ScriptDatabase.CreateAsync();
        await using (var db = database.NewContext())
        {
            db.UserProfiles.Add(new UserProfile { Id = Target, Email = "target@example.com" });
            db.LearningResources.Add(new LearningResource
            {
                Id = "r-chain", UserProfileId = Target, Title = "chain", MediaType = "Vocabulary List",
                Language = "Korean", CreatedAt = At, UpdatedAt = At
            });
            foreach (var id in new[] { "chain-0", "chain-1", "chain-2" })
                db.VocabularyWords.Add(new VocabularyWord
                {
                    Id = id, TargetLanguageTerm = id, LexicalUnitType = LexicalUnitType.Phrase,
                    UserProfileId = Target, CreatedAt = At, UpdatedAt = At
                });
            db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
            {
                Id = "m-chain-0", ResourceId = "r-chain", VocabularyWordId = "chain-0"
            });
            // chain-1 is a part of chain-0 and chain-2 a part of chain-1: each pass resolves one.
            db.PhraseConstituents.AddRange(
                new PhraseConstituent { Id = "pc-0-1", PhraseWordId = "chain-0", ConstituentWordId = "chain-1" },
                new PhraseConstituent { Id = "pc-1-2", PhraseWordId = "chain-1", ConstituentWordId = "chain-2" });
            await db.SaveChangesAsync();
            await db.VocabularyWords.Where(word => word.Id.StartsWith("chain-"))
                .ExecuteUpdateAsync(set => set.SetProperty(word => word.UserProfileId, (string?)null));
        }
        const string AssignScript = "scripts/assign-vocabulary-word-owners.sql";

        var limited = await database.RunAsync(AssignScript, [("max_passes", "1")]);
        Assert.True(limited.ExitCode == 0, limited.Text);
        Assert.Contains("not reached: phrase nesting deeper than the pass limit", limited.Stdout);
        Assert.DoesNotContain("its one learner is not a profile", limited.Stdout);
        Assert.Contains("WARNING: the pass limit (1) stopped phrase resolution", limited.Stdout);

        var refused = await database.RunAsync(AssignScript, [("max_passes", "1"),
            ("expected_ownerless", "3"), ("expected_words", "1"), ("expected_sentences", "0"),
            ("apply", "true")]);
        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains("The pass limit (1) stopped phrase resolution", refused.Stderr);
        Assert.Equal(3, await database.OwnerlessWordsAsync());

        var full = await database.RunAsync(AssignScript, []);
        Assert.True(full.ExitCode == 0, full.Text);
        Assert.Contains("Phrase resolution finished in", full.Stdout);
        var applied = await database.RunAsync(AssignScript, [("expected_ownerless", "3"),
            ("expected_words", "3"), ("expected_sentences", "0"), ("apply", "true")]);
        Assert.True(applied.ExitCode == 0, applied.Text);
        Assert.Equal(0, await database.OwnerlessWordsAsync());
    }

    private static (string Name, string Value)[] RecordArgs(params (string, string)[] extra) =>
        [("target_profile", Target), ("test_domains", TestDomains), .. extra];

    private static (string Name, string Value)[] SendArgs(string tracked, string phrases = "4") =>
    [
        ("expected_words", "5"), ("expected_progress", "4"), ("expected_contexts", "2"),
        ("expected_sentences", "2"), ("expected_phrases", phrases), ("expected_pairs", "1"),
        ("expected_answers", "1"), ("expected_tracked", tracked), ("apply", "true")
    ];

    private sealed record ScriptRun(int ExitCode, string Stdout, string Stderr)
    {
        public string Text => $"exit {ExitCode}\n{Stdout}\n{Stderr}";

        /// <summary>The unaligned row printed right after <paramref name="header"/>.</summary>
        public string[] ValuesAfter(string header)
        {
            var lines = Stdout.Split('\n', StringSplitOptions.TrimEntries);
            var at = Array.IndexOf(lines, header);
            Assert.True(at >= 0 && at + 1 < lines.Length, $"No '{header}' in:\n{Text}");
            return lines[at + 1].Split('|');
        }
    }

    private sealed record TrackedRow(string Table, string Op, string Id, string? Source);

    /// <summary>A disposable database with the model's schema and CoreSync provisioned.</summary>
    private sealed class ScriptDatabase : IAsyncDisposable
    {
        private readonly ApplicationOperationPostgresHarness _harness;

        private ScriptDatabase(ApplicationOperationPostgresHarness harness) => _harness = harness;

        public static async Task<ScriptDatabase> CreateAsync()
        {
            var harness = await ApplicationOperationPostgresHarness.CreateAsync(
                "withheld_rows", migrate: false);
            try
            {
                await using (var db = harness.NewContext())
                {
                    await db.Database.EnsureCreatedAsync();
                    await SyncDeletionLogSchema.ApplyAsync(db);
                }
                var coreSync = new PostgreSQLSyncProvider(
                    new PostgreSQLSyncConfigurationBuilder(harness.ConnectionString)
                        .ConfigureSyncTables().Build(), ProviderMode.Remote);
                await coreSync.ApplyProvisionAsync();
                return new ScriptDatabase(harness);
            }
            catch
            {
                await harness.DisposeAsync();
                throw;
            }
        }

        public ApplicationDbContext NewContext() => _harness.NewContext();

        public ValueTask DisposeAsync() => _harness.DisposeAsync();

        /// <summary>
        /// Target T, another real learner X, a test account K (every email in the test domain),
        /// a profile with one test and one real email (not a test account), a deleted account
        /// (progress only) and an owner that is no profile. Words are saved recorded to T and
        /// then given their real owner directly, as rows saved before words had owners were.
        /// </summary>
        public async Task SeedAsync()
        {
            await using (var db = NewContext())
            {
                db.UserProfiles.AddRange(
                    new UserProfile { Id = Target, Email = "captain@real.example" },
                    new UserProfile { Id = Other, Email = "other@real.example" },
                    new UserProfile { Id = Tester, Email = "kaylee@sentencestudio.test" },
                    new UserProfile { Id = Mixed, Email = "mixed@sentencestudio.test" });
                db.Users.AddRange(
                    new ApplicationUser { Id = "u-t", UserName = "captain", Email = "captain@real.example", UserProfileId = Target },
                    new ApplicationUser { Id = "u-k", UserName = "kaylee", Email = "KAYLEE@SentenceStudio.test", UserProfileId = Tester },
                    new ApplicationUser { Id = "u-mixed", UserName = "mixed", Email = "mixed@gmail.example", UserProfileId = Mixed });
                Resource(db, "rt", Target);
                Resource(db, "rx", Other);
                Resource(db, "rk", Tester);
                Resource(db, "rk2", Mixed);
                Resource(db, "rn", Target);

                // Ownerless words, for the record step.
                Word(db, "w-test1", "rt", "rk");
                Word(db, "w-test2");
                Word(db, "w-phrase-part", "rk");
                Word(db, "w-np1", "rt");
                Word(db, "w-both1", "rt", "rk");
                Word(db, "w-neg-other", "rt", "rx");
                Word(db, "w-neg-ownerless-res", "rt", "rn");
                Word(db, "w-neg-notarget", "rk");
                Word(db, "w-neg-mixed", "rt", "rk2");
                Word(db, "w-neg-pair-gone", "rt");
                Word(db, "w-pair-partner");
                Word(db, "w-target-only", "rt");
                Word(db, "ownerless-in-rt", "rt");
                Word(db, "w-nobody");
                Word(db, "w-neg-sentence-x", "rt", "rk");
                // Recorded words, for the withheld-rows step.
                Word(db, "o-link", "rt");
                Word(db, "o-link2", "rt");
                Word(db, "o-free");
                Word(db, "o-xlinked", "rx");
                Word(db, "xw-free");
                Word(db, "xw-link", "rx");
                Word(db, "ghost-word");

                Progress(db, "p-t-test2", "w-test2", Target);
                Progress(db, "p-k-test2", "w-test2", Tester);
                Progress(db, "p-t-part", "w-phrase-part", Target);
                Progress(db, "p-gone-np1", "w-np1", Gone);
                Progress(db, "p-gone-both1", "w-both1", Gone);
                Progress(db, "p-gone-notarget", "w-neg-notarget", Gone);
                Progress(db, "p-t-free", "o-free", Target);
                Progress(db, "p-t-link", "o-link", Target);
                Progress(db, "p-x-free", "o-free", Other);
                Progress(db, "p-x-xfree", "xw-free", Other);
                Context(db, "c-t-free", "p-t-free");
                Context(db, "c-t-link", "p-t-link");
                Context(db, "c-t-test2", "p-t-test2");
                Context(db, "c-k-test2", "p-k-test2");

                Sentence(db, "s-rec1", "w-test1", null, null);
                Sentence(db, "s-rec2", "w-test1", "rt", null);
                Sentence(db, "s-keep-k", "w-test1", "rk", null);
                Sentence(db, "s-rec3", "w-test2", null, null);
                Sentence(db, "s-x", "w-neg-sentence-x", null, Other);
                Sentence(db, "es-t-free", "o-free", null, Target);
                Sentence(db, "es-t-free-rx", "o-free", "rx", Target);
                Sentence(db, "es-t-link", "o-link", null, Target);
                Sentence(db, "es-x-free", "o-free", null, Other);

                Phrase(db, "ph-free-link", "o-free", "o-link");
                Phrase(db, "ph-link-free", "o-link", "o-free");
                Phrase(db, "ph-link-link", "o-link", "o-link2");
                Phrase(db, "ph-free-null", "o-free", null);
                Phrase(db, "ph-free-x", "o-free", "xw-free");
                // Recording the phrase word makes its part's phrase use name the target.
                Phrase(db, "ph-test1-part", "w-test1", "w-phrase-part");

                db.MinimalPairSessions.AddRange(
                    new MinimalPairSession { Id = "session-t", UserId = Target, StartedAt = At, CreatedAt = At, UpdatedAt = At },
                    new MinimalPairSession { Id = "session-gone", UserId = Gone, StartedAt = At, CreatedAt = At, UpdatedAt = At });
                Pair(db, "mp-t-free-link", Target, "o-free", "o-link");
                Pair(db, "mp-t-link-link", Target, "o-link", "o-link2");
                Pair(db, "mp-x-free-link", Other, "o-free", "o-link");
                Pair(db, "mp-gone", Gone, "w-neg-pair-gone", "w-pair-partner");
                Answer(db, "ma-t-link-free", "mp-t-link-link", "o-link", "o-free");
                Answer(db, "ma-t-link-link", "mp-t-link-link", "o-link", "o-link2");
                await db.SaveChangesAsync();
            }

            await using (var db = NewContext())
            {
                await SetOwnerAsync(db, null, "w-test1", "w-phrase-part", "w-np1", "w-both1",
                    "w-neg-other", "w-neg-ownerless-res", "w-neg-notarget", "w-neg-mixed",
                    "w-neg-pair-gone", "w-pair-partner", "w-target-only", "ownerless-in-rt",
                    "w-nobody", "w-neg-sentence-x");
                await SetOwnerAsync(db, "", "w-test2");
                await SetOwnerAsync(db, Other, "xw-free", "xw-link");
                await SetOwnerAsync(db, Ghost, "ghost-word");
                await db.LearningResources.Where(resource => resource.Id == "rn")
                    .ExecuteUpdateAsync(set => set.SetProperty(
                        resource => resource.UserProfileId, (string?)null));
            }
        }

        public async Task<int> OwnerlessWordsAsync()
        {
            await using var db = NewContext();
            return await db.VocabularyWords.CountAsync(word =>
                word.UserProfileId == null || word.UserProfileId == "");
        }

        public async Task<DateTime> UpdatedAtAsync(string wordId)
        {
            await using var db = NewContext();
            return await db.VocabularyWords.AsNoTracking()
                .Where(word => word.Id == wordId).Select(word => word.UpdatedAt).SingleAsync();
        }

        public async Task<long> MaxTrackedAsync()
        {
            await using var connection = new NpgsqlConnection(_harness.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT coalesce(max(id), 0) FROM __core_sync_ct", connection);
            return Convert.ToInt64(await command.ExecuteScalarAsync());
        }

        public async Task<IReadOnlyList<TrackedRow>> TrackedSinceAsync(long after)
        {
            await using var connection = new NpgsqlConnection(_harness.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT tbl, op, pk_text, src FROM __core_sync_ct WHERE id > @after ORDER BY id",
                connection);
            command.Parameters.AddWithValue("after", after);
            var rows = new List<TrackedRow>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add(new TrackedRow(reader.GetString(0), reader.GetString(1).Trim(),
                    reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            return rows;
        }

        /// <summary>Runs a repository script through psql, credentials in its environment.</summary>
        public async Task<ScriptRun> RunAsync(
            string script, IReadOnlyList<(string Name, string Value)> variables)
        {
            var connection = new NpgsqlConnectionStringBuilder(_harness.ConnectionString);
            var start = new ProcessStartInfo(PsqlScriptPostgresFactAttribute.Psql!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = RepositoryRoot()
            };
            foreach (var argument in new[] { "-X", "-q", "-A", "-F", "|", "-P", "footer=off",
                         "-v", "ON_ERROR_STOP=1" })
                start.ArgumentList.Add(argument);
            foreach (var (name, value) in variables)
            {
                start.ArgumentList.Add("-v");
                start.ArgumentList.Add($"{name}={value}");
            }
            start.ArgumentList.Add("-f");
            start.ArgumentList.Add(script);
            start.Environment["PGHOST"] = connection.Host;
            start.Environment["PGPORT"] = connection.Port.ToString();
            start.Environment["PGDATABASE"] = connection.Database;
            start.Environment["PGUSER"] = connection.Username;
            start.Environment["PGPASSWORD"] = connection.Password ?? string.Empty;
            start.Environment["PGCONNECT_TIMEOUT"] = "15";
            if (connection.SslMode == SslMode.Disable)
                start.Environment["PGSSLMODE"] = "disable";

            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            await process.WaitForExitAsync(timeout.Token);
            return new ScriptRun(process.ExitCode, await stdout, await stderr);
        }

        private static async Task SetOwnerAsync(ApplicationDbContext db, string? owner, params string[] ids) =>
            await db.VocabularyWords.Where(word => ids.Contains(word.Id))
                .ExecuteUpdateAsync(set => set.SetProperty(word => word.UserProfileId, owner));

        private static void Resource(ApplicationDbContext db, string id, string owner) =>
            db.LearningResources.Add(new LearningResource
            {
                Id = id, UserProfileId = owner, Title = id, MediaType = "Vocabulary List",
                Language = "Korean", CreatedAt = At, UpdatedAt = At
            });

        private static void Word(ApplicationDbContext db, string id, params string[] resources)
        {
            db.VocabularyWords.Add(new VocabularyWord
            {
                Id = id, TargetLanguageTerm = id, NativeLanguageTerm = $"{id} meaning",
                Language = "Korean", LexicalUnitType = LexicalUnitType.Word, UserProfileId = Target,
                CreatedAt = At, UpdatedAt = At
            });
            foreach (var resource in resources)
                db.ResourceVocabularyMappings.Add(new ResourceVocabularyMapping
                {
                    Id = $"{resource}-{id}", ResourceId = resource, VocabularyWordId = id
                });
        }

        private static void Progress(ApplicationDbContext db, string id, string word, string user) =>
            db.VocabularyProgresses.Add(new VocabularyProgress
            {
                Id = id, VocabularyWordId = word, UserId = user, TotalAttempts = 1
            });

        private static void Context(ApplicationDbContext db, string id, string progress) =>
            db.VocabularyLearningContexts.Add(new VocabularyLearningContext
            {
                Id = id, VocabularyProgressId = progress, Activity = "VocabularyQuiz",
                InputMode = "TextEntry", LearnedAt = At, CreatedAt = At, UpdatedAt = At
            });

        private static void Sentence(ApplicationDbContext db, string id, string word,
            string? resource, string? owner) =>
            db.ExampleSentences.Add(new ExampleSentence
            {
                Id = id, VocabularyWordId = word, LearningResourceId = resource,
                UserProfileId = owner, TargetSentence = $"{id} sentence", CreatedAt = At, UpdatedAt = At
            });

        private static void Phrase(ApplicationDbContext db, string id, string phrase, string? part) =>
            db.PhraseConstituents.Add(new PhraseConstituent
            {
                Id = id, PhraseWordId = phrase, ConstituentWordId = part
            });

        private static void Pair(ApplicationDbContext db, string id, string user, string a, string b) =>
            db.MinimalPairs.Add(new MinimalPair
            {
                Id = id, UserId = user, VocabularyWordAId = a, VocabularyWordBId = b,
                CreatedAt = At, UpdatedAt = At
            });

        private static void Answer(ApplicationDbContext db, string id, string pair,
            string prompt, string selected) =>
            db.MinimalPairAttempts.Add(new MinimalPairAttempt
            {
                Id = id, UserId = Target, SessionId = "session-t", PairId = pair,
                PromptWordId = prompt, SelectedWordId = selected, SequenceNumber = 1, CreatedAt = At
            });

        private static string RepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null
                && !File.Exists(Path.Combine(directory.FullName, RecordScript)))
                directory = directory.Parent;
            return directory?.FullName
                ?? throw new InvalidOperationException("Could not find the repository root.");
        }
    }
}

/// <summary>
/// A PostgreSQL provider test that also needs the psql client (the scripts use its variables
/// and \if). Skipped when either is unavailable.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class PsqlScriptPostgresFactAttribute : FactAttribute
{
    internal static readonly string? Psql = FindPsql();

    public PsqlScriptPostgresFactAttribute()
    {
        Skip = ApplicationOperationPostgresServer.SkipReason
            ?? (Psql is null ? "psql is not installed (needed to run the repository's SQL scripts)." : null);
    }

    private static string? FindPsql()
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Concat(["/opt/homebrew/bin", "/usr/local/bin", "/usr/bin"]);
        return directories.Select(directory => Path.Combine(directory, "psql"))
            .FirstOrDefault(File.Exists);
    }
}
