// Audio playback interop for server-side Blazor
window.audioInterop = {
    _player: null,
    _timeupdateRef: null,
    _endedRef: null,
    _ready: false,

    playFromBase64: function (base64Data, mimeType) {
        this.stop();
        try {
            var audio = new Audio("data:" + (mimeType || "audio/mpeg") + ";base64," + base64Data);
            this._player = audio;
            this._ready = false;
            audio.addEventListener('canplaythrough', function () {
                window.audioInterop._ready = true;
            }, { once: true });
            audio.play().catch(function (e) {
                console.error("Audio playback failed:", e);
            });
        } catch (e) {
            console.error("Audio playback failed:", e);
        }
    },

    loadFromBase64: function (base64Data, mimeType) {
        var self = this;
        this.stop();
        return new Promise(function (resolve) {
            try {
                var audio = new Audio("data:" + (mimeType || "audio/mpeg") + ";base64," + base64Data);
                self._player = audio;
                self._ready = false;

                audio.addEventListener('canplaythrough', function () {
                    self._ready = true;
                    console.log("[audioInterop] Audio ready, duration:", audio.duration);
                    resolve(audio.duration || 0);
                }, { once: true });

                audio.addEventListener('error', function (e) {
                    console.error("[audioInterop] Audio load error:", e);
                    resolve(0);
                }, { once: true });

                // Trigger load
                audio.load();
            } catch (e) {
                console.error("Audio load failed:", e);
                resolve(0);
            }
        });
    },

    play: function () {
        if (this._player) {
            var p = this._player.play();
            if (p && typeof p.catch === 'function') {
                p.catch(function (e) {
                    console.error("[audioInterop] play() rejected:", e);
                });
            }
        }
    },

    pause: function () {
        if (this._player) {
            this._player.pause();
        }
    },

    seekTo: function (timeSeconds) {
        if (this._player) {
            try {
                this._player.currentTime = timeSeconds;
            } catch (e) {
                console.warn("[audioInterop] seekTo failed:", e);
            }
        }
    },

    getCurrentTime: function () {
        return this._player ? this._player.currentTime : 0;
    },

    getDuration: function () {
        return this._player ? this._player.duration : 0;
    },

    isPlaying: function () {
        return this._player ? !this._player.paused && !this._player.ended : false;
    },

    startTimeTracking: function (dotNetRef, intervalMs) {
        this.stopTimeTracking();
        if (!this._player) return;

        var self = this;
        this._timeupdateRef = setInterval(function () {
            if (self._player && !self._player.paused) {
                dotNetRef.invokeMethodAsync('OnJsTimeUpdate', self._player.currentTime);
            }
        }, intervalMs || 50);

        this._player.onended = function () {
            dotNetRef.invokeMethodAsync('OnJsPlaybackEnded');
            self.stopTimeTracking();
        };
    },

    stopTimeTracking: function () {
        if (this._timeupdateRef) {
            clearInterval(this._timeupdateRef);
            this._timeupdateRef = null;
        }
        if (this._player) {
            this._player.onended = null;
        }
    },

    stop: function () {
        this.stopTimeTracking();
        if (this._player) {
            this._player.pause();
            this._player.currentTime = 0;
            this._player = null;
        }
        this._ready = false;
    }
};
