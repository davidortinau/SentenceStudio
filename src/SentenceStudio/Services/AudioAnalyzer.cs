using System;
using System.IO;
using System.Threading.Tasks;

namespace SentenceStudio.Services;

/// <summary>
/// Service for analyzing audio streams and extracting waveform data for visualization
/// </summary>
public class AudioAnalyzer
{
    private const int DEFAULT_SAMPLE_COUNT = 400; // Fallback sample count
    private const int BYTES_PER_SAMPLE = 2; // 16-bit PCM = 2 bytes per sample
    private const int WAV_HEADER_SIZE = 44; // Standard WAV header size
    
    // Pixels per second of audio - controls the detail level of the waveform
    private const int PIXELS_PER_SECOND = 50; 
    // Minimum number of samples to ensure short audio still has some detail
    private const int MIN_SAMPLE_COUNT = 100;
    // Maximum number of samples to prevent performance issues with very long audio
    private const int MAX_SAMPLE_COUNT = 3000;
    // Threshold below which amplitudes are considered silence (noise floor)
    private const float SILENCE_THRESHOLD = 0.03f;
    // Contrast enhancement factor - amplifies the difference between peaks and silence
    private const float CONTRAST_FACTOR = 1.5f;

    /// <summary>
    /// Analyzes an audio stream and extracts waveform data that accurately represents the entire audio,
    /// including quiet sections and peaks
    /// </summary>
    /// <param name="audioStream">The audio stream to analyze</param>
    /// <param name="sampleRate">The sample rate of the audio in Hz (default 44100)</param>
    /// <param name="token">Cancellation token for async operations</param>
    /// <returns>Array of float values representing the audio waveform</returns>
    public async Task<float[]> AnalyzeAudioStreamAsync(
        Stream audioStream, 
        int sampleRate = 44100, 
        CancellationToken token = default)
    {
        if (audioStream == null || !audioStream.CanRead)
            return new float[DEFAULT_SAMPLE_COUNT]; // Return empty array if stream is invalid

        token.ThrowIfCancellationRequested();
        
        try
        {
            // Save original position to restore later
            var originalPosition = audioStream.Position;
            
            // Ensure we start from the beginning to read the entire stream
            audioStream.Position = 0;

            // For WAV format (assuming 16-bit PCM format which is common for speech)
            // Skip WAV header (44 bytes for standard WAV)
            if (audioStream.Length <= WAV_HEADER_SIZE)
                return GenerateFallbackWaveform();
                
            // Calculate how much audio data we have
            long dataSize = audioStream.Length - WAV_HEADER_SIZE;
            
            // Calculate audio duration in seconds
            long totalSamples = dataSize / BYTES_PER_SAMPLE;
            double durationSeconds = (double)totalSamples / sampleRate;
            
            // Calculate dynamic sample count based on audio duration
            int dynamicSampleCount = (int)(durationSeconds * PIXELS_PER_SECOND);
            
            // Clamp sample count to reasonable min/max values
            int sampleCount = Math.Clamp(dynamicSampleCount, MIN_SAMPLE_COUNT, MAX_SAMPLE_COUNT);
            
            // Calculate stride to evenly sample the entire audio file
            double samplesPerSegment = totalSamples / (double)sampleCount;
            
            if (samplesPerSegment < 1)
            {
                // If we have fewer samples than our target count, we need a different approach
                return await AnalyzeSparseAudioAsync(audioStream, sampleCount, token);
            }
            
            // Move to start of audio data
            audioStream.Position = WAV_HEADER_SIZE;
            
            // Initialize result array
            float[] samples = new float[sampleCount];
            byte[] buffer = new byte[(int)(samplesPerSegment * BYTES_PER_SAMPLE)];
            
            // Process each segment
            for (int i = 0; i < sampleCount; i++)
            {
                token.ThrowIfCancellationRequested();
                
                // Calculate segment boundaries
                long segmentStart = (long)(i * samplesPerSegment);
                int segmentSize = (int)Math.Min(samplesPerSegment * BYTES_PER_SAMPLE, 
                                             audioStream.Length - audioStream.Position);
                
                if (segmentSize <= 0) break;
                
                // Position stream at the beginning of this segment
                long bytePosition = WAV_HEADER_SIZE + (segmentStart * BYTES_PER_SAMPLE);
                audioStream.Position = bytePosition;
                
                // Read the entire segment into the buffer
                int bytesRead = await audioStream.ReadAsync(buffer, 0, Math.Min(buffer.Length, segmentSize), token);
                int samplesRead = bytesRead / BYTES_PER_SAMPLE;
                
                if (samplesRead > 0)
                {
                    // Calculate RMS (Root Mean Square) amplitude for this segment
                    double sumOfSquares = 0;
                    int count = 0;
                    
                    for (int j = 0; j < bytesRead; j += BYTES_PER_SAMPLE)
                    {
                        if (j + 1 < bytesRead)
                        {
                            // Convert bytes to 16-bit signed PCM sample
                            short pcmSample = (short)((buffer[j + 1] << 8) | buffer[j]);

                            // Debug.WriteLine($"Sample {i}: PCM Value = {pcmSample}");
                            
                            // Convert to normalized float (-1.0 to 1.0)
                            float normalizedSample = pcmSample / 32768f;

                            // Debug.WriteLine($"Sample {i}: Normalized Value = {normalizedSample}");
                            
                            // Square the sample and add to sum
                            sumOfSquares += normalizedSample * normalizedSample;
                            count++;
                        }
                    }
                    
                    // Calculate RMS value
                    float rmsValue = count > 0 ? (float)Math.Sqrt(sumOfSquares / count) : 0f;
                    
                    // Apply noise threshold - values below threshold are considered silence
                    rmsValue = rmsValue < SILENCE_THRESHOLD ? 0 : rmsValue;

                    // Debug.WriteLine($"Segment {i}: RMS Value = {rmsValue}");
                    
                    // Store the RMS amplitude for this segment
                    samples[i] = rmsValue;
                }
            }
            
            // Apply a small amount of smoothing to make the waveform more natural looking
            SmoothWaveform(samples);
            
            // Apply contrast enhancement to make the waveform more dynamic
            EnhanceContrast(samples);
            
            // Restore original position
            audioStream.Position = originalPosition;

#if DEBUG
            // Debug output for min and max values
            float minValue = float.MaxValue;
            float maxValue = float.MinValue;

            foreach (var sample in samples)
            {
                if (sample < minValue) minValue = sample;
                if (sample > maxValue) maxValue = sample;
            }

            System.Diagnostics.Debug.WriteLine($"Min Value: {minValue}, Max Value: {maxValue}");
#endif
            return samples;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error analyzing audio: {ex.Message}");
            return GenerateFallbackWaveform(DEFAULT_SAMPLE_COUNT);
        }
    }
    
    /// <summary>
    /// Analyzes a sparse audio stream where there are fewer samples than our target sample count
    /// </summary>
    private async Task<float[]> AnalyzeSparseAudioAsync(
        Stream audioStream, 
        int sampleCount, 
        CancellationToken token = default)
    {
        try
        {
            // Save original position
            var originalPosition = audioStream.Position;
            
            // Move to start of audio data
            audioStream.Position = WAV_HEADER_SIZE;
            
            // Calculate how many samples we actually have
            long dataSize = audioStream.Length - WAV_HEADER_SIZE;
            int actualSampleCount = (int)(dataSize / BYTES_PER_SAMPLE);
            
            // Prepare result array
            float[] samples = new float[sampleCount];
            byte[] buffer = new byte[BYTES_PER_SAMPLE];
            
            // Read all actual samples
            float[] actualSamples = new float[actualSampleCount];
            for (int i = 0; i < actualSampleCount; i++)
            {
                if (await audioStream.ReadAsync(buffer, 0, BYTES_PER_SAMPLE, token) == BYTES_PER_SAMPLE)
                {
                    short pcmSample = (short)((buffer[1] << 8) | buffer[0]);
                    float normalizedSample = pcmSample / 32768f;
                    // Use squared amplitude for RMS consistency with our main method
                    actualSamples[i] = normalizedSample * normalizedSample;
                }
            }
            
            // Interpolate to get our target sample count
            for (int i = 0; i < sampleCount; i++)
            {
                double position = i * (actualSampleCount - 1) / (double)(sampleCount - 1);
                int index = (int)position;
                double fraction = position - index;
                
                if (index < actualSampleCount - 1)
                {
                    float interpolatedValue = (float)(actualSamples[index] * (1 - fraction) + 
                                                    actualSamples[index + 1] * fraction);
                    // Take square root to get RMS value
                    float rmsValue = (float)Math.Sqrt(interpolatedValue);
                    // Apply noise threshold
                    rmsValue = rmsValue < SILENCE_THRESHOLD ? 0 : rmsValue;
                    samples[i] = rmsValue;
                }
                else if (index < actualSampleCount)
                {
                    float rmsValue = (float)Math.Sqrt(actualSamples[index]);
                    samples[i] = rmsValue < SILENCE_THRESHOLD ? 0 : rmsValue;
                }
            }
            
            // Apply smoothing
            SmoothWaveform(samples);
            
            // Restore original position
            audioStream.Position = originalPosition;
            
            return samples;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error analyzing sparse audio: {ex.Message}");
            return GenerateFallbackWaveform(sampleCount);
        }
    }

    /// <summary>
    /// Applies a simple smoothing algorithm to the waveform to make it look more natural
    /// </summary>
    private void SmoothWaveform(float[] samples)
    {
        if (samples == null || samples.Length < 3)
            return;
            
        // Apply a simple moving average (3-point) to smooth the waveform
        float[] original = new float[samples.Length];
        Array.Copy(samples, original, samples.Length);
        
        for (int i = 1; i < samples.Length - 1; i++)
        {
            samples[i] = (original[i - 1] + original[i] * 2 + original[i + 1]) / 4;
        }
    }

    /// <summary>
    /// Generates a fallback waveform when analysis fails
    /// </summary>
    private float[] GenerateFallbackWaveform(int sampleCount = DEFAULT_SAMPLE_COUNT)
    {
        // Create a gentle wave pattern rather than random noise
        var samples = new float[sampleCount];
        for (int i = 0; i < sampleCount; i++)
        {
            double phase = (double)i / sampleCount * Math.PI * 4;
            samples[i] = (float)(Math.Sin(phase) * 0.5f + 0.5f);
        }
        return samples;
    }

    /// <summary>
    /// Enhances the contrast of the waveform to better differentiate between silence and speech
    /// </summary>
    private void EnhanceContrast(float[] samples)
    {
        if (samples == null || samples.Length == 0)
            return;
            
        // Find the minimum and maximum values
        float min = float.MaxValue;
        float max = float.MinValue;
        
        for (int i = 0; i < samples.Length; i++)
        {
            if (samples[i] < min) min = samples[i];
            if (samples[i] > max) max = samples[i];
        }
        
        // If min and max are the same, no contrast enhancement is possible
        if (Math.Abs(max - min) < 0.001f)
            return;
        
        // Calculate range and apply contrast enhancement
        float range = max - min;
        
        // Apply contrast enhancement and normalize to 0-1 range
        for (int i = 0; i < samples.Length; i++)
        {
            // Step 1: Normalize to 0-1 range
            float normalized = (samples[i] - min) / range;
            
            // Step 2: Apply non-linear contrast enhancement (power curve)
            float enhanced = (float)Math.Pow(normalized, 0.7); // Values < 1.0 boost lower amplitudes
            
            // Step 3: Apply additional gain for more dynamic range
            enhanced = enhanced * CONTRAST_FACTOR;
            
            // Clamp to 0-1 range
            samples[i] = Math.Clamp(enhanced, 0f, 1f);
        }
        
        // Apply silence threshold after normalization
        for (int i = 0; i < samples.Length; i++)
        {
            // Apply a more aggressive silence threshold after normalization
            if (samples[i] < SILENCE_THRESHOLD * 0.5f)
            {
                samples[i] = 0f;
            }
        }
    }

    internal async Task<double> GetAudioDurationAsync(Stream stream)
    {
        if (stream == null || !stream.CanRead || stream.Length <= WAV_HEADER_SIZE)
            return 0;

        try
        {
            // Save original position
            var originalPosition = stream.Position;

            // Move to start of audio data
            stream.Position = WAV_HEADER_SIZE;

            // Calculate audio duration
            long dataSize = stream.Length - WAV_HEADER_SIZE;
            long totalSamples = dataSize / BYTES_PER_SAMPLE;
            double durationSeconds = (double)totalSamples / 44100; // Assuming default sample rate of 44100 Hz

            // Restore original position
            stream.Position = originalPosition;

            return durationSeconds;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Error calculating audio duration: {ex.Message}");
            return 0;
        }
    }
}