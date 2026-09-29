import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { describe, it } from 'node:test';
import assert from 'node:assert/strict';

const audioScript = readFileSync(
    new URL('../../src/SentenceStudio.WebApp/wwwroot/js/audioInterop.js', import.meta.url),
    'utf8');

function createAudioInterop(play) {
    const timers = new Map();
    const players = [];
    let nextTimer = 1;

    class FakeAudio {
        constructor(source) {
            this.source = source;
            this.duration = 0.001;
            this.currentTime = 0;
            this.paused = true;
            this.ended = false;
            this.listeners = new Map();
            players.push(this);
        }

        addEventListener(type, callback) { this.listeners.set(type, callback); }
        load() { this.listeners.get('canplaythrough')?.(); }
        play() { return play(this); }
        pause() { this.paused = true; }
        end() {
            this.paused = true;
            this.ended = true;
            this.onended?.();
        }
    }

    const window = {};
    runInNewContext(audioScript, {
        window,
        Audio: FakeAudio,
        console: { log() {}, error() {} },
        setInterval(callback) {
            const id = nextTimer++;
            timers.set(id, callback);
            return id;
        },
        clearInterval(id) { timers.delete(id); }
    });
    return { interop: window.audioInterop, players, timers };
}

describe('browser audio startup and completion', () => {
    it('registers the end callback before play, even when a short clip ends inside play()', async () => {
        const calls = [];
        const { interop, players, timers } = createAudioInterop(audio => {
            assert.equal(typeof audio.onended, 'function');
            audio.end();
            return Promise.resolve();
        });
        const callbacks = {
            invokeMethodAsync(method) {
                calls.push(method);
                return Promise.resolve();
            }
        };

        assert.equal(await interop.loadFromBase64('SUQz', 'audio/mpeg'), 0.001);
        assert.equal(await interop.startPlayback(callbacks, 1000), true);

        assert.equal(players[0].ended, true);
        assert.equal(interop.isPlaying(), false);
        assert.deepEqual(calls, ['OnJsPlaybackEnded']);
        assert.equal(timers.size, 0);
    });

    it('treats an already-ended untracked quiz clip as a successful start', async () => {
        const { interop } = createAudioInterop(audio => {
            audio.end();
            return Promise.resolve();
        });
        await interop.loadFromBase64('SUQz', 'audio/mpeg');

        assert.equal(await interop.startPlayback(null, 1000), true);
        assert.equal(interop.isPlaying(), false);
    });

    it('reports a rejected browser play instead of leaving callbacks running', async () => {
        const { interop, timers } = createAudioInterop(() =>
            Promise.reject(new Error('Playback permission denied')));
        await interop.loadFromBase64('SUQz', 'audio/mpeg');

        await assert.rejects(
            interop.startPlayback({ invokeMethodAsync: () => Promise.resolve() }, 1000),
            /Playback permission denied/);
        assert.equal(timers.size, 0);
    });

    it('ignores a stale ended event after a newer audio request replaces the old player', async () => {
        const calls = [];
        const { interop, players } = createAudioInterop(audio => {
            audio.paused = false;
            return Promise.resolve();
        });
        const callbacks = {
            invokeMethodAsync(method) {
                calls.push(method);
                return Promise.resolve();
            }
        };

        await interop.loadFromBase64('SUQz', 'audio/mpeg');
        await interop.startPlayback(callbacks, 1000);
        const staleEnded = players[0].onended;
        await interop.loadFromBase64('SUQz', 'audio/mpeg');
        await interop.startPlayback(callbacks, 1000);

        staleEnded();
        assert.deepEqual(calls, []);
        assert.equal(interop.isPlaying(), true);
        players[1].end();
        assert.deepEqual(calls, ['OnJsPlaybackEnded']);
    });
});
