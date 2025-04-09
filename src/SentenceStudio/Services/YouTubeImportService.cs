using System;
using System.IO;
using System.Threading.Tasks;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace SentenceStudio.Services;

public class YouTubeImportService
{
    private readonly YoutubeClient _youtubeClient;
    private readonly AudioAnalyzer _audioAnalyzer;
    
    public YouTubeImportService(AudioAnalyzer audioAnalyzer)
    {
        _youtubeClient = new YoutubeClient();
        _audioAnalyzer = audioAnalyzer;
    }
    
    /// <summary>
    /// Extracts audio from a YouTube video URL
    /// </summary>
    /// <param name="videoUrl">YouTube video URL</param>
    /// <param name="startTime">Start time in seconds</param>
    /// <param name="duration">Duration to extract in seconds</param>
    /// <returns>Audio stream and metadata</returns>
    public async Task<StreamHistory> ExtractAudioClipAsync(
        string videoUrl, 
        double startTime, 
        double duration)
    {
        try
        {
            // Parse the video ID from the URL
            var videoId = YoutubeExplode.Videos.VideoId.Parse(videoUrl);
            
            // Get video metadata
            var video = await _youtubeClient.Videos.GetAsync(videoId);
            
            // Get available media streams
            var streamManifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId);
            
            // Get the audio-only stream with highest quality
            var audioStreamInfo = streamManifest
                .GetAudioOnlyStreams()
                .Where(s => s.AudioCodec.StartsWith("mp4", StringComparison.OrdinalIgnoreCase))
                .GetWithHighestBitrate();
                
            if (audioStreamInfo == null)
                throw new Exception("No suitable audio stream found");
                
            // Download the full audio stream
            var fullAudioStream = await _youtubeClient.Videos.Streams.GetAsync(audioStreamInfo);
            
            // Create a memory stream to hold our clipped audio
            var clippedAudioStream = new MemoryStream();
            
            // Use FFmpeg to extract the specific clip
            // using (var process = new System.Diagnostics.Process())
            // {
            //     process.StartInfo.FileName = "ffmpeg";
            //     process.StartInfo.Arguments = $"-i pipe:0 -ss {startTime} -t {duration} -c:a pcm_s16le -ar 44100 -ac 1 -f wav pipe:1";
            //     process.StartInfo.UseShellExecute = false;
            //     process.StartInfo.RedirectStandardInput = true;
            //     process.StartInfo.RedirectStandardOutput = true;
            //     process.StartInfo.CreateNoWindow = true;
                
            //     process.Start();
                
            //     // Copy the input stream to FFmpeg
            //     await fullAudioStream.CopyToAsync(process.StandardInput.BaseStream);
            //     process.StandardInput.Close();
                
            //     // Read the output stream from FFmpeg
            //     await process.StandardOutput.BaseStream.CopyToAsync(clippedAudioStream);
                
            //     process.WaitForExit();
            // }
            
            // Reset stream position
            fullAudioStream.Position = 0;
            
            // Analyze the waveform
            var waveformData = await _audioAnalyzer.GetWaveformAsync(fullAudioStream, 1200);
            
            // Reset stream position again
            fullAudioStream.Position = 0;
            
            // Create the stream history object
            return new StreamHistory
            {
                FileName = $"youtube_{videoId}_{startTime}_{duration}.wav",
                Title = video.Title,
                Source = "YouTube",
                SourceUrl = videoUrl,
                Duration = duration,
                WaveformData = waveformData,
                Stream = clippedAudioStream
            };
        }
        catch (Exception ex)
        {
            throw new Exception($"Failed to extract audio from YouTube: {ex.Message}", ex);
        }
    }
}