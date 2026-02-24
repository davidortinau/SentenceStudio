using System.Buffers;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NLayer.NAudioSupport; // ➜ NuGet: NLayer.NAudioSupport (adds MP3 decoding everywhere)
using Microsoft.Extensions.Logging;

namespace SentenceStudio.Services
{
    /// <summary>
    /// Cross‑platform service that decodes an MP3 stream (or file) to PCM and
    /// returns a fixed‑width <c>float[]</c> ready for waveform drawing.
    ///
    /// Dependencies (add via NuGet):
    ///   • NAudio (>= 2.2)
    ///   • NLayer.NAudioSupport (pure‑C# MP3 decoder that plugs into NAudio)
    /// </summary>
    public sealed class AudioAnalyzer
    {
        private const int DEFAULT_SAMPLE_RATE = 44_100; // Hz
        private const float SILENCE_DB = -60f;          // gate threshold (dBFS)
        private readonly float _silenceLinear = (float)Math.Pow(10, SILENCE_DB / 20);
        private readonly ILogger<AudioAnalyzer> _logger;

        public AudioAnalyzer(ILogger<AudioAnalyzer> logger)
        {
            _logger = logger;
        }



        // ──────────────────────────────────────────────────────────────────────────
        //  Public API
        // ──────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Last decoded audio length (seconds). Updated after every call to
        /// <see cref="GetWaveformAsync"/>.
        /// </summary>
        public double LastDurationSeconds { get; private set; }

        /// <summary>
        /// Decodes <paramref name="mp3Stream"/> and returns <paramref name="columns"/>
        /// equally‑spaced amplitude samples (0–1).
        /// </summary>
        /// <param name="mp3Stream">Seekable MP3 data stream.</param>
        /// <param name="columns">How many bars/pixels you intend to draw.</param>
        /// <param name="ct">Cancellation token.</param>
        public async Task<float[]> GetWaveformAsync(Stream mp3Stream,
                                                    int columns,
                                                    CancellationToken ct = default)
        {
            if (mp3Stream == null || !mp3Stream.CanRead)
                throw new ArgumentException("Invalid MP3 stream", nameof(mp3Stream));

            // Ensure we have a seekable stream (Mp3FileReader requires it)
            if (!mp3Stream.CanSeek)
            {
                var copy = new MemoryStream();
                await mp3Stream.CopyToAsync(copy, ct).ConfigureAwait(false);
                copy.Position = 0;
                mp3Stream = copy;
            }

            // 1) ─ Decode MP3 ➜ float PCM (mono, 44.1 kHz) ───────────────────────
            using var decoder = CreateDecoder(mp3Stream);
            var sampleProvider = decoder.ToSampleProvider(); // 32‑bit float samples

            // Down‑mix to mono if necessary (many TTS services are already mono)
            if (sampleProvider.WaveFormat.Channels > 1)
                sampleProvider = new StereoToMonoSampleProvider(sampleProvider);

            // At this point we have 44.1 kHz, 1‑channel, 32‑bit float PCM.

            // Pull the entire decoded stream into a pooled buffer
            int totalSamples = (int)(decoder.Length / sizeof(float));
            var pcm = ArrayPool<float>.Shared.Rent(totalSamples);

            int read = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                int n = sampleProvider.Read(pcm, read, totalSamples - read);
                if (n == 0) break;
                read += n;
            }

            LastDurationSeconds = read / 44_100f; // 44.1 kHz mono

            // 2) ─ Down‑sample to "columns" buckets ──────────────────────────────
            var waveform = DownSample(pcm, read, columns);

            ArrayPool<float>.Shared.Return(pcm);
            return waveform;
        }

        /// <summary>
        /// Convenience wrapper for file paths (e.g. local cache of TTS result).
        /// </summary>
        public async Task<float[]> GetWaveformAsync(string filePath,
                                                    int columns,
                                                    CancellationToken ct = default)
        {
            await using var fs = File.OpenRead(filePath);
            return await GetWaveformAsync(fs, columns, ct).ConfigureAwait(false);
        }

        // ──────────────────────────────────────────────────────────────────────────
        //  Internals
        // ──────────────────────────────────────────────────────────────────────────

        private static WaveStream CreateDecoder(Stream mp3Stream)
        {
            // Mp3FileReader uses the delegate to create an IMp3FrameDecompressor.
            // We inject NLayer's pure‑managed one so it works on every platform.
            return new Mp3FileReaderBase(mp3Stream, wf => new Mp3FrameDecompressor(wf));
        }

        private static float[] DownSample(float[] pcm, int length, int columns)
        {
            const float SilenceDb = -60f;                         // gate at –60 dBFS
            float silenceLin = (float)Math.Pow(10, SilenceDb / 20);

            var result = new float[columns];
            double window = (double)length / columns;            // samples per bucket

            for (int col = 0; col < columns; col++)
            {
                int start = (int)(col * window);
                int end = (int)Math.Min(length, (col + 1) * window);

                float peak = 0f;
                double sumSq = 0;
                int count = 0;

                for (int i = start; i < end; i++)
                {
                    float s = Math.Abs(pcm[i]);
                    peak = Math.Max(peak, s);
                    sumSq += s * s;
                    count++;
                }

                if (count == 0)
                    continue;

                float rms = (float)Math.Sqrt(sumSq / count);
                float value = Math.Max(peak, rms * 1.2f);        // keep quiet parts visible

                result[col] = value < silenceLin ? 0f : value;  // noise gate
            }

            return result;
        }
        
        /// <summary>
        /// Returns the exact duration (in seconds) of an MP3 stream.
        /// </summary>
        /// <remarks>
        /// Works for any seekable <see cref="Stream"/>.  If the incoming stream is not
        /// seekable (e.g., a network stream), it is first copied to a temporary
        /// <see cref="MemoryStream"/>.
        /// </remarks>
        public async Task<double> GetDurationAsync(Stream mp3Stream,
                                                   CancellationToken ct = default)
        {
            if (mp3Stream == null || !mp3Stream.CanRead)
                return 0;

            // Ensure seekable input --------------------------------------------------
            Stream seekable = mp3Stream;
            bool disposeSeekable = false;
            if (!mp3Stream.CanSeek)
            {
                seekable = new MemoryStream();
                await mp3Stream.CopyToAsync(seekable, ct);
                seekable.Position = 0;
                disposeSeekable = true;
            }

            try
            {
                using var reader = new Mp3FileReader(seekable);
                return reader.TotalTime.TotalSeconds;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting audio duration");
                return 0;
            }
            finally
            {
                if (disposeSeekable)
                    seekable.Dispose();
            }
        }
    }
}
