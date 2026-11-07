using Microsoft.Extensions.DependencyInjection;
using SentenceStudio.Services.Progress;
using SentenceStudio.Services.Realtime;
using SentenceStudio.Shared.Models;

namespace SentenceStudio.Native.Tests;

/// <summary>
/// Conversation voice mode (the Techorama demo's Conversation.Voice) on the native presenter:
/// real SQLite, the seeded source scenarios and the owner's words, with fake session and audio ports.
/// </summary>
public sealed class NativeConversationVoiceTests
{
    private const string Conversation = NativeActivityCatalog.Conversation;

    [Fact]
    public async Task WithoutVoicePorts_VoiceIsNotOffered()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture);
        using var conversation = ActivatorUtilities.CreateInstance<NativeConversationPresenter>(
            fixture.Provider, new FakeConversationAi(), new ScratchShadowingSpeech(), new ScratchShadowingPlayer());
        await conversation.OpenAsync(Launch(fixture));
        var ready = await conversation.Opening;

        Assert.False(ready.IsVoiceAvailable);
        Assert.False(ready.CanStartVoice);
        var after = await conversation.StartVoiceAsync();
        Assert.Equal(NativeConversationVoice.Off, after.Voice);
    }

    [Fact]
    public async Task Journey_SpokenTurnsJoinTheChatAndStayAfterEnd()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 3);
        await SignInAsync(fixture);
        var ai = new FakeConversationAi();
        var voice = new FakeVoice();
        var audio = new FakeAudio();
        using var conversation = Presenter(fixture, ai, voice, audio);
        await conversation.OpenAsync(Launch(fixture));
        var ready = await conversation.Opening;
        Assert.True(ready.CanStartVoice);
        Assert.Equal("최수진", ready.PartnerName);

        var started = await conversation.StartVoiceAsync();

        Assert.Equal(NativeConversationVoice.Listening, started.Voice);
        Assert.False(started.CanEdit);
        var request = Assert.Single(voice.Requests);
        Assert.Equal(NativeSliceFixture.UserA, request.Owner);
        Assert.Equal("최수진", request.Request.Scenario?.PersonaName);
        Assert.Equal("Korean", request.Request.TargetLanguage);
        Assert.Equal(["opening 1"], request.Request.RecentTurns.Select(turn => turn.Text));
        Assert.Equal(3, request.Request.Vocabulary.Count);
        Assert.All(request.Request.Vocabulary, word => Assert.Equal(NativeSliceFixture.UserA, word.UserProfileId));
        var session = voice.Session!;
        Assert.True(session.Began);
        Assert.True(audio.Started);
        Assert.Equal(new ConversationVoicePace(1f, TimeSpan.FromMilliseconds(500)), request.Request.Pace);

        // The partner first reads its opening line aloud (the session leaves that turn's transcript out).
        Assert.Equal("opening 1", session.ReadAloud);

        // Partner speaks: audio plays, the transcript builds one bubble with the vocabulary lookup.
        session.ToolInvoked?.Invoke("커피", ["커피 = coffee"]);
        session.AudioDelta?.Invoke(Convert.ToBase64String([1, 2, 3, 4]));
        session.AssistantTranscriptDelta?.Invoke("p1", "커피 ");
        session.AssistantTranscriptDelta?.Invoke("p1", "드릴까요?");
        await WaitForAsync(() => conversation.State.Messages.Length == 2
            && conversation.State.Messages[1].Text == "커피 드릴까요?");
        var speaking = conversation.State;
        Assert.Equal(NativeConversationVoice.PartnerSpeaking, speaking.Voice);
        Assert.Equal([1, 2, 3, 4], Assert.Single(audio.Played));
        Assert.Equal("opening 1", speaking.Messages[0].Text);
        Assert.False(speaking.Messages[0].IsVoice);
        var partner = speaking.Messages[1];
        Assert.True(partner.IsVoice);
        Assert.False(partner.IsUser);
        Assert.False(partner.CanListen);
        Assert.Equal("최수진", partner.Author);
        var note = Assert.Single(partner.ToolNotes);
        Assert.Equal("커피", note.Term);
        Assert.Equal(["커피 = coffee"], note.Matches.AsEnumerable());

        // Playback ends: the learner's turn.
        audio.PlaybackIdle!();
        await WaitForAsync(() => conversation.State.Voice == NativeConversationVoice.Listening);

        // The learner speaks: microphone audio goes to the session in order; the transcript lands.
        audio.Microphone!([9]);
        audio.Microphone!([8]);
        audio.Level!(0.42);
        session.SpeechStarted?.Invoke();
        session.UserTurnStarted?.Invoke("u1");
        await WaitForAsync(() => conversation.State.Messages.Length == 3);
        Assert.True(conversation.State.Messages[2].IsTranscribing);
        Assert.Equal(0.4, conversation.State.MicLevel, 3);
        Assert.Equal(1, audio.Cleared);
        session.UserTranscriptCompleted?.Invoke("u1", "아이스 아메리카노 주세요");
        // Noise committed as a turn is dropped; a turn that could not be transcribed waits as "…".
        session.UserTurnStarted?.Invoke("u2");
        session.UserTranscriptCompleted?.Invoke("u2", "...");
        session.UserTurnStarted?.Invoke("u3");
        session.UserTranscriptCompleted?.Invoke("u3", "");
        await WaitForAsync(() => conversation.State.Messages.Length == 4
            && conversation.State.Messages[2].Text == "아이스 아메리카노 주세요");
        Assert.True(conversation.State.Messages[2].IsUser);
        Assert.Equal(NativeConversationPresenter.Untranscribed, conversation.State.Messages[3].Text);
        await WaitForAsync(() => session.Appended.Count == 2);
        Assert.Equal([[9], [8]], session.Appended);

        var ended = await conversation.StopVoiceAsync();

        Assert.Equal(NativeConversationVoice.Off, ended.Voice);
        Assert.True(audio.Stopped);
        await WaitForAsync(() => session.Disposed);
        Assert.Equal(["opening 1", "커피 드릴까요?", "아이스 아메리카노 주세요"],
            ended.Messages.Select(message => message.Text));
        Assert.True(ended.CanEdit);

        // Late events from the ended session change nothing.
        session.AssistantTranscriptDelta?.Invoke("p2", "late");
        session.AudioDelta?.Invoke(Convert.ToBase64String([7]));
        await Task.Delay(250);
        Assert.Equal(3, conversation.State.Messages.Length);
        Assert.Single(audio.Played);

        // Typing continues the same thread, spoken turns included; voice turns are not scored.
        conversation.SetDraft("감사합니다");
        await conversation.SendAsync();
        Assert.Equal(["opening 1", "커피 드릴까요?", "아이스 아메리카노 주세요", "감사합니다"],
            ai.Continues.Single().History.Select(turn => turn.Item2));
        Assert.Equal(0, await fixture.AttemptCountAsync(NativeSliceFixture.UserA));
    }

    [Fact]
    public async Task AfterAFailedReply_ThePartnerAnswersInsteadOfReadingAloud()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture);
        var ai = new FakeConversationAi { FailReplies = true };
        var voice = new FakeVoice();
        using var conversation = Presenter(fixture, ai, voice, new FakeAudio());
        await conversation.OpenAsync(Launch(fixture));
        await conversation.Opening;
        conversation.SetDraft("안녕하세요");
        var failed = await conversation.SendAsync();
        Assert.True(failed.Messages[^1].IsUser);

        await conversation.StartVoiceAsync();

        Assert.True(voice.Session!.Began);
        Assert.Null(voice.Session.ReadAloud);
    }

    [Fact]
    public async Task VoicePace_IsKeptPerOwnerAndChangesTheRunningSession()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture);
        var voice = new FakeVoice();
        using (var conversation = Presenter(fixture, new FakeConversationAi(), voice, new FakeAudio()))
        {
            await conversation.OpenAsync(Launch(fixture));
            var ready = await conversation.Opening;
            Assert.Equal(100, ready.VoiceSpeedPercent);
            Assert.Equal(500, ready.VoicePauseMilliseconds);

            // Only the offered choices are taken.
            Assert.Equal(100, (await conversation.SetVoiceSpeedAsync(85)).VoiceSpeedPercent);
            Assert.Equal(500, (await conversation.SetVoicePauseAsync(700)).VoicePauseMilliseconds);

            Assert.Equal(80, (await conversation.SetVoiceSpeedAsync(80)).VoiceSpeedPercent);
            await conversation.StartVoiceAsync();
            Assert.Equal(new ConversationVoicePace(0.8f, TimeSpan.FromMilliseconds(500)),
                voice.Requests.Single().Request.Pace);
            var session = voice.Session!;
            Assert.Empty(session.Paces);

            // A change while voice runs goes to the session.
            var paused = await conversation.SetVoicePauseAsync(1500);
            Assert.Equal(1500, paused.VoicePauseMilliseconds);
            Assert.Equal(new ConversationVoicePace(0.8f, TimeSpan.FromMilliseconds(1500)), Assert.Single(session.Paces));

            // A session that refuses the update keeps voice running; the choice is still saved.
            session.FailPace = true;
            var kept = await conversation.SetVoicePauseAsync(2500);
            Assert.Equal(NativeConversationVoice.Listening, kept.Voice);
            Assert.Equal(2500, kept.VoicePauseMilliseconds);
            Assert.Equal(2500, fixture.Preferences.Get(
                NativeConversationPresenter.VoicePauseKey(NativeSliceFixture.UserA), 0));
            await conversation.SetVoicePauseAsync(1500);
            await conversation.ExitAsync();
        }

        Assert.Equal(80, fixture.Preferences.Get(
            NativeConversationPresenter.VoiceSpeedKey(NativeSliceFixture.UserA), 0));
        Assert.Equal(0, fixture.Preferences.Get(
            NativeConversationPresenter.VoiceSpeedKey(NativeSliceFixture.UserB), 0));

        // The next visit starts with the owner's saved pace.
        using var again = Presenter(fixture, new FakeConversationAi(), voice, new FakeAudio());
        await again.OpenAsync(Launch(fixture));
        var reopened = await again.Opening;
        Assert.Equal(80, reopened.VoiceSpeedPercent);
        Assert.Equal(1500, reopened.VoicePauseMilliseconds);
    }

    [Fact]
    public async Task VoicePace_ChangedWhileConnecting_IsSentOnceTheSessionOpens()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture);
        var voice = new FakeVoice { Hold = new TaskCompletionSource() };
        using var conversation = Presenter(fixture, new FakeConversationAi(), voice, new FakeAudio());
        await conversation.OpenAsync(Launch(fixture));
        await conversation.Opening;

        var starting = conversation.StartVoiceAsync();
        await WaitForAsync(() => voice.Requests.Count == 1);
        Assert.Equal(NativeConversationVoice.Connecting, conversation.State.Voice);
        await conversation.SetVoiceSpeedAsync(70);
        voice.Hold.SetResult();
        var started = await starting;

        Assert.Equal(NativeConversationVoice.Listening, started.Voice);
        Assert.Equal(ConversationVoicePace.Default, voice.Requests.Single().Request.Pace);
        Assert.Equal(new ConversationVoicePace(0.7f, TimeSpan.FromMilliseconds(500)), Assert.Single(voice.Session!.Paces));
        Assert.True(voice.Session.Began);
    }

    [Fact]
    public async Task VoicePace_AnUnreadableSetting_FallsBackToTheDefaults()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture);
        using var conversation = ActivatorUtilities.CreateInstance<NativeConversationPresenter>(fixture.Provider,
            new FakeConversationAi(), new ScratchShadowingSpeech(), new ScratchShadowingPlayer(),
            new FakeVoice(), new FakeAudio(), new ThrowingPreferences());

        await conversation.OpenAsync(Launch(fixture));
        var ready = await conversation.Opening;

        Assert.Equal(NativePhase.Ready, ready.Phase);
        Assert.Equal(100, ready.VoiceSpeedPercent);
        Assert.Equal(500, ready.VoicePauseMilliseconds);
    }

    [Fact]
    public async Task MicrophoneDenied_TellsTheLearnerAndOpensNoSession()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture);
        var voice = new FakeVoice();
        var audio = new FakeAudio { Granted = false };
        using var conversation = Presenter(fixture, new FakeConversationAi(), voice, audio);
        await conversation.OpenAsync(Launch(fixture));
        await conversation.Opening;

        var state = await conversation.StartVoiceAsync();

        Assert.Equal(NativeConversationVoice.Off, state.Voice);
        Assert.Equal(NativeConversationText.VoiceMicDenied, Assert.Single(state.Notices).Key);
        Assert.Empty(voice.Requests);
        Assert.False(audio.Started);
    }

    [Fact]
    public async Task SessionCannotOpen_TellsTheLearnerAndLeavesTheChatUsable()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture);
        var voice = new FakeVoice { Fail = true };
        var audio = new FakeAudio();
        using var conversation = Presenter(fixture, new FakeConversationAi(), voice, audio);
        await conversation.OpenAsync(Launch(fixture));
        await conversation.Opening;

        var state = await conversation.StartVoiceAsync();

        Assert.Equal(NativeConversationVoice.Off, state.Voice);
        Assert.Equal(NativeConversationText.VoiceUnavailable, Assert.Single(state.Notices).Key);
        Assert.False(audio.Started);
        Assert.True(state.CanEdit);
    }

    [Fact]
    public async Task SessionEnds_VoiceTurnsOffWithANotice()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture);
        var voice = new FakeVoice();
        var audio = new FakeAudio();
        using var conversation = Presenter(fixture, new FakeConversationAi(), voice, audio);
        await conversation.OpenAsync(Launch(fixture));
        await conversation.Opening;
        await conversation.StartVoiceAsync();

        voice.Session!.Ended?.Invoke();

        await WaitForAsync(() => conversation.State.Voice == NativeConversationVoice.Off);
        Assert.Equal(NativeConversationText.VoiceUnavailable, Assert.Single(conversation.State.Notices).Key);
        Assert.True(audio.Stopped);
        await WaitForAsync(() => voice.Session.Disposed);
    }

    [Fact]
    public async Task BackAndScenarioChange_EndVoice()
    {
        using var fixture = new NativeSliceFixture();
        await fixture.SeedAsync(NativeSliceFixture.UserA, wordCount: 1);
        await SignInAsync(fixture);
        var ai = new FakeConversationAi();
        var voice = new FakeVoice();
        var audio = new FakeAudio();
        using var conversation = Presenter(fixture, ai, voice, audio);
        await conversation.OpenAsync(Launch(fixture));
        var ready = await conversation.Opening;
        await conversation.StartVoiceAsync();
        var first = voice.Session!;

        var restarted = await conversation.SelectScenarioAsync(ready.Scenarios[1].Id);

        Assert.Equal(NativeConversationVoice.Off, restarted.Voice);
        Assert.True(audio.Stopped);
        await WaitForAsync(() => first.Disposed);

        audio.Stopped = false;
        await conversation.StartVoiceAsync();
        var second = voice.Session!;
        Assert.NotSame(first, second);

        var left = await conversation.ExitAsync();

        Assert.Equal(NativeDestination.TodaysPlan, left.Destination);
        Assert.True(audio.Stopped);
        await WaitForAsync(() => second.Disposed);
        second.AssistantTranscriptDelta?.Invoke("late", "late");
        await Task.Delay(250);
        Assert.Equal(NativeDestination.TodaysPlan, conversation.State.Destination);
    }

    [Fact]
    public async Task Instructions_UseTheScenarioLanguageAndRecentTurns()
    {
        var scenario = new ConversationScenario
        {
            Name = "Ordering Coffee", PersonaName = "박지영",
            PersonaDescription = "a friendly barista", SituationDescription = "Ordering coffee",
            ConversationType = ConversationType.Finite
        };
        var turns = Enumerable.Range(1, 10).Select(i => new ConversationChunk
        {
            Role = i % 2 == 0 ? ConversationRole.User : ConversationRole.Assistant,
            Text = $"turn {i}"
        }).ToList();

        var instructions = await NativeConversationVoiceService.InstructionsAsync(
            new(scenario, "Korean", turns, []), CancellationToken.None);

        Assert.Contains("You are playing the role of 박지영, a friendly barista.", instructions);
        Assert.Contains("Speak only Korean", instructions);
        Assert.Contains("transactional conversation", instructions);
        Assert.DoesNotContain("Learner: turn 2\n", instructions);
        Assert.Contains("박지영: turn 3", instructions);
        Assert.Contains("Learner: turn 10", instructions);

        var fallback = await NativeConversationVoiceService.InstructionsAsync(
            new(null, "Korean", [], []), CancellationToken.None);
        Assert.Contains($"You are playing the role of {NativeConversationAi.DefaultPersonaName}", fallback);
        Assert.DoesNotContain("Conversation so far", fallback);
    }

    private static NativeConversationPresenter Presenter(NativeSliceFixture fixture,
        INativeConversationAi ai, INativeConversationVoice voice, INativeVoiceAudio audio) =>
        ActivatorUtilities.CreateInstance<NativeConversationPresenter>(fixture.Provider, ai,
            new ScratchShadowingSpeech(), new ScratchShadowingPlayer(), voice, audio);

    private static NativeActivityLaunch Launch(NativeSliceFixture fixture) =>
        new(Conversation, NativeSliceFixture.UserA, new DateOnly(2026, 9, 28), null,
            [fixture.ResourceId(NativeSliceFixture.UserA)], null, [], false,
            fixture.ResourceId(NativeSliceFixture.UserA), PlanActivityType.Conversation);

    private static async Task SignInAsync(NativeSliceFixture fixture) =>
        Assert.Equal(NativePhase.Ready,
            (await fixture.Auth.SignInAsync(NativeSliceFixture.EmailA, "fixture-password")).Phase);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 150 && !condition(); i++)
            await Task.Delay(20);
        Assert.True(condition());
    }

    private sealed class ThrowingPreferences : SentenceStudio.Abstractions.IPreferencesService
    {
        public T Get<T>(string key, T defaultValue) => throw new InvalidOperationException("Synthetic preferences failure.");
        public void Set<T>(string key, T value) => throw new InvalidOperationException("Synthetic preferences failure.");
        public void Remove(string key) { }
        public void Clear() { }
    }

    private sealed class FakeConversationAi : INativeConversationAi
    {
        public List<(List<(string?, string?)> History, string Language)> Continues { get; } = [];
        public bool FailReplies { get; set; }
        private int _starts;

        public Task<string?> StartAsync(string owner, ConversationScenario? scenario,
            string targetLanguage, CancellationToken cancellationToken) =>
            Task.FromResult<string?>($"opening {++_starts}");

        public Task<Reply?> ContinueAsync(string owner, IReadOnlyList<ConversationChunk> chunks,
            ConversationScenario? scenario, string targetLanguage, CancellationToken cancellationToken)
        {
            Continues.Add((chunks.Select(chunk => (chunk.Author, chunk.Text)).ToList(), targetLanguage));
            return Task.FromResult<Reply?>(FailReplies ? null : new Reply { Message = "reply", Comprehension = 80 });
        }
    }

    private sealed class FakeVoice : INativeConversationVoice
    {
        public List<(string Owner, NativeConversationVoiceRequest Request)> Requests { get; } = [];
        public FakeSession? Session { get; private set; }
        public bool Fail { get; set; }
        public TaskCompletionSource? Hold { get; set; }

        public async Task<IConversationVoiceSession> StartAsync(string owner,
            NativeConversationVoiceRequest request, CancellationToken cancellationToken)
        {
            Requests.Add((owner, request));
            if (Hold is not null)
                await Hold.Task;
            if (Fail)
                throw new HttpRequestException("Synthetic realtime offline.");
            Session = new FakeSession();
            return Session;
        }
    }

    private sealed class FakeSession : IConversationVoiceSession
    {
        public Action<string>? AudioDelta;
        public Action? SpeechStarted;
        public Action<string>? UserTurnStarted;
        public Action<string, string>? UserTranscriptCompleted;
        public Action<string, string>? AssistantTranscriptDelta;
        public Action<string, string>? AssistantTranscriptCompleted;
        public Action<string, IReadOnlyList<string>>? ToolInvoked;
        public Action<string>? Failed;
        public Action? Ended;
        public bool Began { get; private set; }
        public string? ReadAloud { get; private set; }
        public List<ConversationVoicePace> Paces { get; } = [];
        public bool FailPace { get; set; }
        public bool Disposed { get; private set; }
        public List<byte[]> Appended { get; } = [];

        event Action<string>? IConversationVoiceSession.AudioDelta { add => AudioDelta += value; remove => AudioDelta -= value; }
        event Action? IConversationVoiceSession.SpeechStarted { add => SpeechStarted += value; remove => SpeechStarted -= value; }
        event Action<string>? IConversationVoiceSession.UserTurnStarted { add => UserTurnStarted += value; remove => UserTurnStarted -= value; }
        event Action<string, string>? IConversationVoiceSession.UserTranscriptCompleted { add => UserTranscriptCompleted += value; remove => UserTranscriptCompleted -= value; }
        event Action<string, string>? IConversationVoiceSession.AssistantTranscriptDelta { add => AssistantTranscriptDelta += value; remove => AssistantTranscriptDelta -= value; }
        event Action<string, string>? IConversationVoiceSession.AssistantTranscriptCompleted { add => AssistantTranscriptCompleted += value; remove => AssistantTranscriptCompleted -= value; }
        event Action<string, IReadOnlyList<string>>? IConversationVoiceSession.ToolInvoked { add => ToolInvoked += value; remove => ToolInvoked -= value; }
        event Action<string>? IConversationVoiceSession.Failed { add => Failed += value; remove => Failed -= value; }
        event Action? IConversationVoiceSession.Ended { add => Ended += value; remove => Ended -= value; }

        public Task BeginAsync(string? readAloud = null)
        {
            Began = true;
            ReadAloud = readAloud;
            return Task.CompletedTask;
        }

        public Task SetPaceAsync(ConversationVoicePace pace)
        {
            if (FailPace)
                throw new System.Net.WebSockets.WebSocketException("Synthetic session update failure.");
            Paces.Add(pace);
            return Task.CompletedTask;
        }

        public Task AppendAudioAsync(byte[] pcm16)
        {
            lock (Appended)
                Appended.Add(pcm16);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeAudio : INativeVoiceAudio
    {
        public bool Granted { get; set; } = true;
        public bool Started { get; private set; }
        public bool Stopped { get; set; }
        public int Cleared { get; private set; }
        public List<byte[]> Played { get; } = [];
        public Action<byte[]>? Microphone { get; private set; }
        public Action<double>? Level { get; private set; }
        public Action? PlaybackIdle { get; private set; }

        public Task<bool> RequestMicrophoneAsync(CancellationToken cancellationToken) => Task.FromResult(Granted);

        public Task StartAsync(Action<byte[]> microphone, Action<double> level, Action playbackIdle)
        {
            Started = true;
            Microphone = microphone;
            Level = level;
            PlaybackIdle = playbackIdle;
            return Task.CompletedTask;
        }

        public void Play(byte[] pcm16) => Played.Add(pcm16);

        public void ClearPlayback() => Cleared++;

        public void Stop() => Stopped = true;
    }
}
