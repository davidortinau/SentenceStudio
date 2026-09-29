using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using SentenceStudio.Abstractions;
using SentenceStudio.Data;
using SentenceStudio.Services.Plans;
using SentenceStudio.Shared.Models;
using SentenceStudio.WebUI.Pages;
using SentenceStudio.WebUI.Services;

namespace SentenceStudio.UI.Tests.Audio;

public class HowDoYouSayDeletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletePlayingHistory_BrowserStopThrows_StillDeletesPersistedRow(bool disconnected)
    {
        await using var fixture = await HistoryFixture.CreateAsync();
        var stopError = disconnected
            ? (Exception)new JSDisconnectedException("Browser circuit disconnected")
            : new JSException("Browser rejected audioInterop.stop");
        var rowPresentWhenStopAttempted = false;
        var js = new ThrowingStopJsRuntime(stopError,
            () => rowPresentWhenStopAttempted = fixture.RowExists(fixture.DeletedRow.Id));
        var logger = new RecordingLogger<HowDoYouSay>();
        var toasts = new List<ToastMessage>();
        var page = fixture.CreatePlayingPage(js, logger, toasts);

        await InvokeDeleteAsync(page, fixture.DeletedRow);

        js.StopCalls.Should().Be(1);
        rowPresentWhenStopAttempted.Should().BeTrue("browser stop precedes the repository delete");
        var remaining = await fixture.Repository.GetAllStreamHistoryAsync();
        remaining.Select(row => row.Id).Should().Equal(fixture.OtherRow.Id);
        GetField<List<StreamHistory>>(page, "history")
            .Select(row => row.Id).Should().Equal(fixture.OtherRow.Id);
        GetField<StreamHistory?>(page, "currentPlayingItem").Should().BeNull();
        GetField<bool>(page, "isAudioPlaying").Should().BeFalse();
        toasts.Should().ContainSingle().Which
            .Should().Be(new ToastMessage("Deleted.", ToastType.Success, 3000));
        var warning = logger.Entries.Should().ContainSingle().Which;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Error.Should().BeSameAs(stopError);
        warning.Message.Should().Contain("continuing deletion");
        warning.Message.Contains("JS circuit disconnected").Should().Be(disconnected);
        fixture.RepositoryLogger.Entries.Should().BeEmpty();
        await page.DisposeAsync();
        js.StopCalls.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletePlayingHistory_AsyncStopRejectsOrCancels_StillDeletesPersistedRow(bool cancelStop)
    {
        await using var fixture = await HistoryFixture.CreateAsync();
        var stopError = cancelStop
            ? (Exception)new TaskCanceledException("Browser audio stop canceled")
            : new InvalidOperationException("Browser audio stop failed unexpectedly");
        var rowPresentWhenStopAttempted = false;
        var js = new ThrowingStopJsRuntime(stopError,
            () => rowPresentWhenStopAttempted = fixture.RowExists(fixture.DeletedRow.Id),
            cancelStop ? StopFailureMode.Cancel : StopFailureMode.Reject);
        var logger = new RecordingLogger<HowDoYouSay>();
        var toasts = new List<ToastMessage>();
        var page = fixture.CreatePlayingPage(js, logger, toasts);

        await InvokeDeleteAsync(page, fixture.DeletedRow);

        js.StopCalls.Should().Be(1);
        rowPresentWhenStopAttempted.Should().BeTrue("browser stop precedes the repository delete");
        (await fixture.Repository.GetAllStreamHistoryAsync())
            .Select(row => row.Id).Should().Equal(fixture.OtherRow.Id);
        GetField<List<StreamHistory>>(page, "history")
            .Select(row => row.Id).Should().Equal(fixture.OtherRow.Id);
        GetField<StreamHistory?>(page, "currentPlayingItem").Should().BeNull();
        GetField<bool>(page, "isAudioPlaying").Should().BeFalse();
        toasts.Should().ContainSingle().Which
            .Should().Be(new ToastMessage("Deleted.", ToastType.Success, 3000));
        var warning = logger.Entries.Should().ContainSingle().Which;
        warning.Level.Should().Be(LogLevel.Warning);
        if (cancelStop)
            warning.Error.Should().BeOfType<TaskCanceledException>();
        else
            warning.Error.Should().BeSameAs(stopError);
        warning.Message.Should().Contain("continuing deletion");
        warning.Message.Should().NotContain("Failed to delete");
        fixture.RepositoryLogger.Entries.Should().BeEmpty();

        await page.DisposeAsync();
        js.StopCalls.Should().Be(1);
    }

    [Fact]
    public async Task DeletePlayingHistory_NativeStopThrows_StillDeletesPersistedRow()
    {
        await using var fixture = await HistoryFixture.CreateAsync();
        var stopError = new InvalidOperationException("Native audio stop failed");
        var player = new FailingStopAudioPlayer(stopError);
        var js = new ThrowingStopJsRuntime(new JSException("Unexpected browser stop"), () => { });
        var logger = new RecordingLogger<HowDoYouSay>();
        var toasts = new List<ToastMessage>();
        var page = fixture.CreatePlayingPage(js, logger, toasts);
        SetMember(page, "_browserAudioAttempted", false);
        var audioSession = GetField<UiAudioPlayerSession>(page, "_nativeAudio");
        var stream = new MemoryStream([0x01]);
        audioSession.CreatePlayer(new SingleAudioPlayerFactory(player), stream);

        await InvokeDeleteAsync(page, fixture.DeletedRow);

        js.StopCalls.Should().Be(0);
        player.Disposed.Should().BeTrue();
        stream.CanRead.Should().BeFalse();
        (await fixture.Repository.GetAllStreamHistoryAsync())
            .Select(row => row.Id).Should().Equal(fixture.OtherRow.Id);
        GetField<List<StreamHistory>>(page, "history")
            .Select(row => row.Id).Should().Equal(fixture.OtherRow.Id);
        GetField<StreamHistory?>(page, "currentPlayingItem").Should().BeNull();
        GetField<bool>(page, "isAudioPlaying").Should().BeFalse();
        toasts.Should().ContainSingle().Which
            .Should().Be(new ToastMessage("Deleted.", ToastType.Success, 3000));
        var warning = logger.Entries.Should().ContainSingle().Which;
        warning.Level.Should().Be(LogLevel.Warning);
        warning.Error.Should().BeSameAs(stopError);
        warning.Message.Should().Contain("continuing deletion");
        fixture.RepositoryLogger.Entries.Should().BeEmpty();

        await page.DisposeAsync();
    }

    [Theory]
    [InlineData(StopFailureMode.Throw, true)]
    [InlineData(StopFailureMode.Reject, false)]
    [InlineData(StopFailureMode.Cancel, false)]
    public async Task DisposeAsync_AudioStopFails_DisposesLifetimeAndKeepsHistory(
        StopFailureMode stopMode, bool disconnected)
    {
        await using var fixture = await HistoryFixture.CreateAsync();
        var stopError = disconnected
            ? (Exception)new JSDisconnectedException("Browser circuit disconnected")
            : new JSException("Browser rejected audioInterop.stop");
        var js = new ThrowingStopJsRuntime(stopError,
            () => fixture.RowExists(fixture.DeletedRow.Id).Should().BeTrue(), stopMode);
        var logger = new RecordingLogger<HowDoYouSay>();
        var toasts = new List<ToastMessage>();
        var page = fixture.CreatePlayingPage(js, logger, toasts);
        var lifetime = GetField<CancellationTokenSource>(page, "_audioLifetime");

        await page.DisposeAsync();

        js.StopCalls.Should().Be(1);
        lifetime.IsCancellationRequested.Should().BeTrue();
        Action readToken = () => { _ = lifetime.Token; };
        readToken.Should().Throw<ObjectDisposedException>();
        GetField<StreamHistory?>(page, "currentPlayingItem").Should().BeNull();
        GetField<bool>(page, "isAudioPlaying").Should().BeFalse();
        (await fixture.Repository.GetAllStreamHistoryAsync())
            .Select(row => row.Id).Should().BeEquivalentTo(
                new[] { fixture.DeletedRow.Id, fixture.OtherRow.Id });
        GetField<List<StreamHistory>>(page, "history")
            .Select(row => row.Id).Should().BeEquivalentTo(
                new[] { fixture.DeletedRow.Id, fixture.OtherRow.Id });
        toasts.Should().BeEmpty();
        var warning = logger.Entries.Should().ContainSingle().Which;
        warning.Level.Should().Be(LogLevel.Warning);
        if (stopMode == StopFailureMode.Cancel)
            warning.Error.Should().BeOfType<TaskCanceledException>();
        else
            warning.Error.Should().BeSameAs(stopError);
        warning.Message.Should().Contain("during disposal");
        warning.Message.Should().NotContain("delete");
        fixture.RepositoryLogger.Entries.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletePlayingHistory_RepositoryRejectsDelete_KeepsRowAndReportsError(bool cancelStop)
    {
        await using var fixture = await HistoryFixture.CreateAsync();
        await fixture.BlockDeletesAsync();
        var stopError = cancelStop
            ? (Exception)new TaskCanceledException("Browser audio stop canceled")
            : new JSException("Browser rejected audioInterop.stop");
        var js = new ThrowingStopJsRuntime(stopError, () =>
            fixture.RowExists(fixture.DeletedRow.Id).Should().BeTrue(),
            cancelStop ? StopFailureMode.Cancel : StopFailureMode.Throw);
        var logger = new RecordingLogger<HowDoYouSay>();
        var toasts = new List<ToastMessage>();
        var page = fixture.CreatePlayingPage(js, logger, toasts);

        await InvokeDeleteAsync(page, fixture.DeletedRow);

        js.StopCalls.Should().Be(1);
        var persisted = await fixture.Repository.GetAllStreamHistoryAsync();
        persisted.Select(row => row.Id).Should().BeEquivalentTo(
            new[] { fixture.DeletedRow.Id, fixture.OtherRow.Id });
        GetField<List<StreamHistory>>(page, "history")
            .Select(row => row.Id).Should().BeEquivalentTo(
                new[] { fixture.DeletedRow.Id, fixture.OtherRow.Id });
        toasts.Should().ContainSingle().Which
            .Should().Be(new ToastMessage("Error deleting item.", ToastType.Error, 5000));
        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Warning
            && (cancelStop
                ? entry.Error is TaskCanceledException
                : ReferenceEquals(entry.Error, stopError)));
        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Error
            && typeof(InvalidOperationException).IsInstanceOfType(entry.Error)
            && entry.Message.Contains("Failed to delete history item"));
        fixture.RepositoryLogger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Error
            && entry.Error != null
            && entry.Message.Contains("Error occurred in DeleteStreamHistoryAsync"));
        await page.DisposeAsync();
        js.StopCalls.Should().Be(1);
    }

    [Fact]
    public async Task DeleteHistory_WhenAnotherUserOwnsId_LeavesBothUsersRowsUntouched()
    {
        await using var fixture = await HistoryFixture.CreateAsync();
        var js = new ThrowingStopJsRuntime(
            new JSException("Unexpected browser stop"), () => { });
        var logger = new RecordingLogger<HowDoYouSay>();
        var toasts = new List<ToastMessage>();
        var page = fixture.CreatePlayingPage(js, logger, toasts);

        await InvokeDeleteAsync(page, fixture.ForeignRow);

        js.StopCalls.Should().Be(0, "denying a foreign row must not stop unrelated playback");
        fixture.RowExists(fixture.DeletedRow.Id).Should().BeTrue();
        fixture.RowExists(fixture.ForeignRow.Id).Should().BeTrue();
        (await fixture.Repository.GetAllStreamHistoryAsync())
            .Select(row => row.Id).Should().BeEquivalentTo(
                new[] { fixture.DeletedRow.Id, fixture.OtherRow.Id });
        GetField<List<StreamHistory>>(page, "history")
            .Select(row => row.Id).Should().BeEquivalentTo(
                new[] { fixture.DeletedRow.Id, fixture.OtherRow.Id });
        toasts.Should().ContainSingle().Which.Type.Should().Be(ToastType.Error);
        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Error && entry.Message.Contains("Failed to delete"));

        await page.DisposeAsync();
    }

    [Fact]
    public async Task DeleteHistory_AfterOwnerChanges_StopsStalePlaybackAndShowsOnlyNewOwnersRows()
    {
        await using var fixture = await HistoryFixture.CreateAsync();
        var js = new ThrowingStopJsRuntime(
            new JSException("Browser stop failed"), () => { });
        var logger = new RecordingLogger<HowDoYouSay>();
        var toasts = new List<ToastMessage>();
        var page = fixture.CreatePlayingPage(js, logger, toasts);
        fixture.UserScope.ActiveId = HistoryFixture.OwnerB;

        await InvokeDeleteAsync(page, fixture.DeletedRow);

        js.StopCalls.Should().Be(1);
        fixture.RowExists(fixture.DeletedRow.Id).Should().BeTrue();
        fixture.RowExists(fixture.ForeignRow.Id).Should().BeTrue();
        GetField<List<StreamHistory>>(page, "history")
            .Select(row => row.Id).Should().Equal(fixture.ForeignRow.Id);
        GetField<StreamHistory?>(page, "currentPlayingItem").Should().BeNull();
        GetField<bool>(page, "isAudioPlaying").Should().BeFalse();
        toasts.Should().ContainSingle().Which.Type.Should().Be(ToastType.Error);
        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Warning && entry.Message.Contains("continuing deletion"));
        logger.Entries.Should().ContainSingle(entry =>
            entry.Level == LogLevel.Error && entry.Message.Contains("Failed to delete"));

        await page.DisposeAsync();
    }

    [Fact]
    public async Task PlayHistory_WhenAnotherUserOwnsId_DoesNotLoadTheirAudio()
    {
        await using var fixture = await HistoryFixture.CreateAsync();
        var js = new SuccessfulStopJsRuntime();
        var toasts = new List<ToastMessage>();
        var page = fixture.CreatePlayingPage(js, new RecordingLogger<HowDoYouSay>(), toasts);

        await InvokePlayAsync(page, fixture.ForeignRow);

        js.StopCalls.Should().Be(0);
        GetField<List<StreamHistory>>(page, "history")
            .Select(row => row.Id).Should().BeEquivalentTo(
                new[] { fixture.DeletedRow.Id, fixture.OtherRow.Id });
        toasts.Should().ContainSingle().Which
            .Should().Be(new ToastMessage("Audio unavailable.", ToastType.Warning, 4000));
        fixture.RowExists(fixture.ForeignRow.Id).Should().BeTrue();

        await page.DisposeAsync();
    }

    [Fact]
    public async Task PlayHistory_AfterOwnerChanges_StopsOldAudioAndHidesOldHistory()
    {
        await using var fixture = await HistoryFixture.CreateAsync();
        var js = new SuccessfulStopJsRuntime();
        var toasts = new List<ToastMessage>();
        var page = fixture.CreatePlayingPage(js, new RecordingLogger<HowDoYouSay>(), toasts);
        fixture.UserScope.ActiveId = HistoryFixture.OwnerB;

        await InvokePlayAsync(page, fixture.DeletedRow);

        js.StopCalls.Should().Be(1);
        GetField<List<StreamHistory>>(page, "history")
            .Select(row => row.Id).Should().Equal(fixture.ForeignRow.Id);
        GetField<StreamHistory?>(page, "currentPlayingItem").Should().BeNull();
        GetField<bool>(page, "isAudioPlaying").Should().BeFalse();
        toasts.Should().ContainSingle().Which.Type.Should().Be(ToastType.Warning);

        await page.DisposeAsync();
    }

    [Fact]
    public async Task DeleteHistoryHandler_PreservesSharedScratchAudioFile()
    {
        var scratchAudio = Path.GetTempFileName();
        byte[] originalBytes = [0x49, 0x44, 0x33, 0x01];
        try
        {
            await File.WriteAllBytesAsync(scratchAudio, originalBytes);
            await using var fixture = await HistoryFixture.CreateAsync(scratchAudio);
            var js = new SuccessfulStopJsRuntime();
            var toasts = new List<ToastMessage>();
            var page = fixture.CreatePlayingPage(js, new RecordingLogger<HowDoYouSay>(), toasts);

            await InvokeDeleteAsync(page, fixture.DeletedRow);
            await page.DisposeAsync();

            fixture.RowExists(fixture.DeletedRow.Id).Should().BeFalse();
            fixture.RowExists(fixture.ForeignRow.Id).Should().BeTrue();
            (await fixture.Repository.GetAllStreamHistoryAsync())
                .Select(row => row.Id).Should().Equal(fixture.OtherRow.Id);
            fixture.UserScope.ActiveId = HistoryFixture.OwnerB;
            (await fixture.Repository.GetStreamHistoryAsync(fixture.ForeignRow.Id))!
                .AudioFilePath.Should().Be(scratchAudio);
            toasts.Should().ContainSingle().Which.Type.Should().Be(ToastType.Success);
            File.Exists(scratchAudio).Should().BeTrue(
                "deleting an owned history row must not remove audio shared with another owner");
            (await File.ReadAllBytesAsync(scratchAudio)).Should().Equal(originalBytes);
        }
        finally
        {
            File.Delete(scratchAudio);
        }
    }

    private static Task InvokeDeleteAsync(HowDoYouSay page, StreamHistory row) =>
        (Task)typeof(HowDoYouSay)
            .GetMethod("DeleteHistoryItem", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [row])!;

    private static Task InvokePlayAsync(HowDoYouSay page, StreamHistory row) =>
        (Task)typeof(HowDoYouSay)
            .GetMethod("PlayHistoryItem", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [row])!;

    private static void SetMember(HowDoYouSay page, string name, object? value)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var property = typeof(HowDoYouSay).GetProperty(name, flags);
        if (property is not null)
        {
            property.SetValue(page, value);
            return;
        }

        var field = typeof(HowDoYouSay).GetField(name, flags)
            ?? throw new InvalidOperationException($"Missing component member: {name}");
        field.SetValue(page, value);
    }

    private static T GetField<T>(HowDoYouSay page, string name) =>
        (T)typeof(HowDoYouSay)
            .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(page)!;

    private sealed class HistoryFixture : IAsyncDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private ServiceProvider _services = null!;

        public const string OwnerA = "history-user-a";
        public const string OwnerB = "history-user-b";
        public SwitchableUserScope UserScope { get; } = new() { ActiveId = OwnerA };
        public RecordingLogger<StreamHistoryRepository> RepositoryLogger { get; } = new();
        public StreamHistoryRepository Repository { get; private set; } = null!;
        public StreamHistory DeletedRow { get; private set; } = null!;
        public StreamHistory OtherRow { get; private set; } = null!;
        public StreamHistory ForeignRow { get; private set; } = null!;

        public static async Task<HistoryFixture> CreateAsync(string? sharedAudioPath = null)
        {
            var fixture = new HistoryFixture();
            await fixture._connection.OpenAsync();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<ApplicationDbContext>(
                options => options.UseSqlite(fixture._connection));
            services.AddSingleton<IUserScopeProvider>(fixture.UserScope);
            fixture._services = services.BuildServiceProvider();
            fixture.Repository = new StreamHistoryRepository(
                fixture._services, fixture.RepositoryLogger);

            await using var scope = fixture._services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.EnsureCreatedAsync();
            db.UserProfiles.AddRange(
                new UserProfile { Id = OwnerA, CreatedAt = DateTime.UtcNow.AddDays(-2) },
                new UserProfile { Id = OwnerB, CreatedAt = DateTime.UtcNow.AddDays(-2) });
            fixture.DeletedRow = new StreamHistory
            {
                Phrase = "Phrase to delete",
                AudioFilePath = sharedAudioPath,
                UserProfileId = OwnerA,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            fixture.OtherRow = new StreamHistory
            {
                Phrase = "Unrelated phrase",
                UserProfileId = OwnerA,
                CreatedAt = DateTime.UtcNow.AddMinutes(-1),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-1)
            };
            fixture.ForeignRow = new StreamHistory
            {
                Phrase = "Another user's phrase",
                AudioFilePath = sharedAudioPath,
                UserProfileId = OwnerB,
                CreatedAt = DateTime.UtcNow.AddMinutes(-2),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-2)
            };
            db.StreamHistories.AddRange(fixture.DeletedRow, fixture.OtherRow, fixture.ForeignRow);
            await db.SaveChangesAsync();
            return fixture;
        }

        public HowDoYouSay CreatePlayingPage(
            IJSRuntime js, RecordingLogger<HowDoYouSay> logger, List<ToastMessage> toasts)
        {
            var toast = new ToastService();
            toast.OnShow += toasts.Add;
            var page = new HowDoYouSay();
            SetMember(page, "Localize", new BlazorLocalizationService());
            SetMember(page, "HistoryRepo", Repository);
            SetMember(page, "JS", js);
            SetMember(page, "Logger", logger);
            SetMember(page, "Toast", toast);
            SetMember(page, "history", new List<StreamHistory> { DeletedRow, OtherRow });
            SetMember(page, "currentPlayingItem", DeletedRow);
            SetMember(page, "isAudioPlaying", true);
            SetMember(page, "_browserAudioAttempted", true);
            return page;
        }

        public bool RowExists(int id)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = """SELECT COUNT(*) FROM "StreamHistory" WHERE "Id" = $id""";
            command.Parameters.AddWithValue("$id", id);
            return Convert.ToInt64(command.ExecuteScalar()) != 0;
        }

        public async Task BlockDeletesAsync()
        {
            await using var scope = _services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER BlockStreamHistoryDelete
                BEFORE DELETE ON "StreamHistory"
                BEGIN
                    SELECT RAISE(FAIL, 'Fixture rejects history deletion');
                END;
                """);
        }

        public async ValueTask DisposeAsync()
        {
            await _services.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class SwitchableUserScope : IUserScopeProvider
    {
        public string? ActiveId { get; set; }

        public string UserProfileId =>
            TryGetUserProfileId(out var id) ? id : throw new UnauthorizedAccessException();

        public bool TryGetUserProfileId(out string userProfileId)
        {
            userProfileId = ActiveId ?? string.Empty;
            return !string.IsNullOrWhiteSpace(userProfileId);
        }
    }

    public enum StopFailureMode { Throw, Reject, Cancel }

    private sealed class SuccessfulStopJsRuntime : IJSRuntime
    {
        public int StopCalls { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier != "audioInterop.stop")
                throw new InvalidOperationException($"Unexpected JS call: {identifier}");
            StopCalls++;
            return ValueTask.FromResult(default(TValue)!);
        }
    }

    private sealed class ThrowingStopJsRuntime(
        Exception stopError, Action onStop, StopFailureMode failureMode = StopFailureMode.Throw) : IJSRuntime
    {
        public int StopCalls { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier != "audioInterop.stop")
                throw new InvalidOperationException($"Unexpected JS call: {identifier}");

            StopCalls++;
            onStop();
            return failureMode switch
            {
                StopFailureMode.Reject => new ValueTask<TValue>(Task.FromException<TValue>(stopError)),
                StopFailureMode.Cancel => new ValueTask<TValue>(
                    Task.FromCanceled<TValue>(new CancellationToken(canceled: true))),
                _ => throw stopError
            };
        }
    }

    private sealed class SingleAudioPlayerFactory(IUiAudioPlayer player) : IAudioPlayerFactory
    {
        public bool CanPlayLocally => true;
        public IUiAudioPlayer CreatePlayer(Stream audioStream) => player;
    }

    private sealed class FailingStopAudioPlayer(Exception stopError) : IUiAudioPlayer
    {
        public event EventHandler? PlaybackEnded { add { } remove { } }

        public bool IsPlaying => true;
        public double CurrentPosition => 0;
        public double Duration => 0;
        public bool Disposed { get; private set; }

        public void Play() { }
        public void Pause() { }
        public void Stop() => throw stopError;
        public void Seek(double seconds) { }
        public void Dispose() => Disposed = true;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, Exception? Error, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(
            LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((level, exception, formatter(state, exception)));
    }
}
