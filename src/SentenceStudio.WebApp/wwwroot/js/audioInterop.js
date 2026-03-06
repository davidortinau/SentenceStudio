// Audio playback interop for server-side Blazor
window.audioInterop = {
    _player: null,

    playFromBase64: function (base64Data, mimeType) {
        this.stop();
        try {
            var audio = new Audio("data:" + (mimeType || "audio/mpeg") + ";base64," + base64Data);
            this._player = audio;
            audio.play();
        } catch (e) {
            console.error("Audio playback failed:", e);
        }
    },

    stop: function () {
        if (this._player) {
            this._player.pause();
            this._player.currentTime = 0;
            this._player = null;
        }
    }
};
