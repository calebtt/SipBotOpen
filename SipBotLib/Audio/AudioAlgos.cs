using NAudio.Codecs;
using NAudio.Dsp;
using NAudio.Wave;
using Serilog;

namespace SipBot;
public static class AudioAlgos
{
    /// <summary>
    /// Resamples PCM audio to the target sample rate, handling variable input sizes.
    /// </summary>
    public static byte[] ResamplePcmWithNAudio(byte[] inputPcm, int inputSampleRate, int outputSampleRate)
    {
        // Handle odd-length input by padding
        if (inputPcm.Length % 2 != 0)
        {
            Log.Warning($"Invalid input PCM length: {inputPcm.Length} bytes (must be even for 16-bit), padding with 1 byte");
            byte[] paddedInput = new byte[inputPcm.Length + 1];
            Array.Copy(inputPcm, paddedInput, inputPcm.Length);
            inputPcm = paddedInput;
        }

        // Calculate expected output size
        int inputSamples = inputPcm.Length / 2; // 16-bit mono
        double sampleRateRatio = (double)outputSampleRate / inputSampleRate;
        int expectedOutputSamples = (int)Math.Ceiling(inputSamples * sampleRateRatio);
        // Ensure even output samples for 16-bit audio
        if (expectedOutputSamples % 2 != 0)
            expectedOutputSamples++;

        using var inputStream = new MemoryStream(inputPcm);
        using var rawSource = new RawSourceWaveStream(inputStream, new WaveFormat(inputSampleRate, 16, 1));
        var outFormat = new WaveFormat(outputSampleRate, 16, 1);

        using var conversionStream = new WaveFormatConversionStream(outFormat, rawSource);
        using var outputStream = new MemoryStream();
        byte[] buffer = new byte[4096];
        int bytesRead;

        while ((bytesRead = conversionStream.Read(buffer, 0, buffer.Length)) > 0)
        {
            outputStream.Write(buffer, 0, bytesRead);
        }

        byte[] resampled = outputStream.ToArray();

        // Ensure output is even-length and padded to expected size
        if (resampled.Length % 2 != 0)
        {
            byte[] paddedOutput = new byte[resampled.Length + 1];
            Array.Copy(resampled, paddedOutput, resampled.Length);
            resampled = paddedOutput;
        }

        if (resampled.Length < expectedOutputSamples * 2)
        {
            byte[] paddedOutput = new byte[expectedOutputSamples * 2];
            Array.Copy(resampled, paddedOutput, resampled.Length);
            resampled = paddedOutput;
        }
        else if (resampled.Length > expectedOutputSamples * 2)
        {
            byte[] trimmedOutput = new byte[expectedOutputSamples * 2];
            Array.Copy(resampled, trimmedOutput, trimmedOutput.Length);
            resampled = trimmedOutput;
        }

        return resampled;
    }

    /// <summary>
    /// Convert a WAV file (as byte array) to raw PCM (16-bit, mono) at a specified target sample rate.
    /// Note that it reads the WAV file header for information on the source audio's sample rate.
    /// </summary>
    /// <param name="wavAudio">WAV file data</param>
    /// <param name="targetSampleRateHz">Desired output sample rate (e.g. 8000, 16000)</param>
    /// <returns>Raw PCM byte array (16-bit mono)</returns>
    public static byte[] ConvertWavToPcm(byte[] wavAudio, int targetSampleRateHz)
    {
        try
        {
            using var wavStream = new MemoryStream(wavAudio);
            using var reader = new WaveFileReader(wavStream);

            var inputFormat = reader.WaveFormat;
            var targetFormat = new WaveFormat(targetSampleRateHz, 16, 1);

            using var resampler = new MediaFoundationResampler(reader, targetFormat)
            {
                ResamplerQuality = 60
            };

            using var outStream = new MemoryStream();
            byte[] buffer = new byte[4096];
            int bytesRead;
            while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
            {
                outStream.Write(buffer, 0, bytesRead);
            }

            byte[] rawPcm = outStream.ToArray();
            return rawPcm;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to convert WAV to raw PCM.");
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Encodes 16 bit mono PCM data, irrespective of sample rate (this encoding algo doesn't depend on it.)
    /// </summary>
    public static byte[] EncodePcmToPcmuWithNAudio(byte[] pcmSamples)
    {
        if (pcmSamples.Length % 2 != 0)
        {
            Log.Warning($"[EncodePcmToPcmuWithNAudio] PCM samples length {pcmSamples.Length} is not even, trimming last byte.");
            Array.Resize(ref pcmSamples, pcmSamples.Length - 1);
        }

        int sampleCount = pcmSamples.Length / 2;
        byte[] pcmu = new byte[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            // Combine two bytes into one short (little-endian)
            short sample = (short)(pcmSamples[i * 2] | (pcmSamples[i * 2 + 1] << 8));
            pcmu[i] = MuLawEncoder.LinearToMuLawSample(sample);
        }

        return pcmu;
    }

    /// <summary>
    /// Converts MP3 audio to 8kHz 16-bit mono PCMU encoded.
    /// </summary>
    /// <param name="mp3Stream">MemoryStream containing MP3 data</param>
    /// <returns>PCMU-encoded byte array</returns>
    public static byte[] ConvertMp3ToPcmu(MemoryStream mp3Stream)
    {
        if (!mp3Stream.CanSeek)
        {
            throw new ArgumentException("Stream must be seekable.");
        }

        try
        {
            // Convert MP3 to WAV first
            using var mp3Reader = new Mp3FileReader(mp3Stream);
            using var wavStream = new MemoryStream();
            WaveFileWriter.WriteWavFileToStream(wavStream, mp3Reader);
            wavStream.Position = 0;

            // Use existing AudioAlgos functions to convert WAV to PCMU
            byte[] wavData = wavStream.ToArray();
            byte[] pcmData = ConvertWavToPcm(wavData, 8000);
            byte[] pcmuData = EncodePcmToPcmuWithNAudio(pcmData);

            Log.Debug($"Converted MP3 to PCMU: {pcmuData.Length} bytes");
            return pcmuData;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to convert MP3 to PCMU.");
            return Array.Empty<byte>();
        }
    }

    public static byte[]? ReadWelcomeFileMp3BytesAsPcmu(string fileName)
    {
        if (File.Exists(fileName))
        {
            var fileBytes = File.ReadAllBytes(fileName);
            return ConvertMp3ToPcmu(new(fileBytes));
        }
        Log.Information($"No welcome message mp3 file bytes could be read for: {fileName}");
        return null;
    }

    /// <summary>
    /// Reads a WAV file and converts it to 8kHz PCMU bytes.
    /// </summary>
    public static byte[] ReadWelcomeWavBytesAsPcmu(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                Log.Warning($"Welcome WAV file not found: {filePath}");
                return Array.Empty<byte>();
            }

            using var wavStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using var reader = new WaveFileReader(wavStream);
            var targetFormat = new WaveFormat(8000, 16, 1);
            using var conversionStream = WaveFormatConversionStream.CreatePcmStream(reader);
            using var resampler = new WaveFormatConversionStream(targetFormat, conversionStream);

            using var outStream = new MemoryStream();
            byte[] buffer = new byte[4096];
            int bytesRead;
            while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
            {
                outStream.Write(buffer, 0, bytesRead);
            }

            byte[] pcm8kHz = outStream.ToArray();

            // Encode to PCMU
            if (pcm8kHz.Length % 2 != 0)
            {
                Log.Warning($"PCM samples length {pcm8kHz.Length} is not even, trimming last byte.");
                Array.Resize(ref pcm8kHz, pcm8kHz.Length - 1);
            }

            int sampleCount = pcm8kHz.Length / 2;
            byte[] pcmu = new byte[sampleCount];

            for (int i = 0; i < sampleCount; i++)
            {
                short sample = (short)(pcm8kHz[i * 2] | (pcm8kHz[i * 2 + 1] << 8));
                pcmu[i] = MuLawEncoder.LinearToMuLawSample(sample);
            }

            Log.Debug($"Converted WAV to PCMU: {pcmu.Length} bytes");
            return pcmu;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Failed to read and convert welcome WAV file: {filePath}");
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Reads a WAV file and converts it to 16kHz PCM bytes for STT processing.
    /// </summary>
    public static byte[] ReadWelcomeWavBytesAsPcm16kHz(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                Log.Warning($"Welcome WAV file not found: {filePath}");
                return Array.Empty<byte>();
            }

            using var wavStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            using var reader = new WaveFileReader(wavStream);

            // Resample to 16kHz mono 16-bit PCM
            var targetFormat = new WaveFormat(16000, 16, 1);
            using var resampler = new MediaFoundationResampler(reader, targetFormat)
            {
                ResamplerQuality = 60
            };

            using var outStream = new MemoryStream();
            byte[] buffer = new byte[4096];
            int bytesRead;
            while ((bytesRead = resampler.Read(buffer, 0, buffer.Length)) > 0)
            {
                outStream.Write(buffer, 0, bytesRead);
            }

            byte[] pcm16kHz = outStream.ToArray();
            Log.Debug($"Converted WAV to 16kHz PCM: {pcm16kHz.Length} bytes");
            return pcm16kHz;
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"Failed to read and convert welcome WAV file to 16kHz PCM: {filePath}");
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Converts PCMU (G.711 μ-law) encoded audio to 16-bit 16kHz mono PCM.
    /// </summary>
    /// <param name="pcmuAudio">PCMU-encoded byte array</param>
    /// <returns>16-bit 16kHz mono PCM byte array</returns>
    public static byte[] ConvertPcmuToPcm16kHz(byte[] pcmuAudio)
    {
        try
        {
            // Step 1: Decode PCMU to 16-bit PCM at 8kHz
            int sampleCount = pcmuAudio.Length;
            byte[] pcm8kHz = new byte[sampleCount * 2]; // 16-bit = 2 bytes per sample

            for (int i = 0; i < sampleCount; i++)
            {
                // Decode mu-law sample to 16-bit linear PCM
                short linearSample = MuLawDecoder.MuLawToLinearSample(pcmuAudio[i]);
                // Write as little-endian 16-bit sample
                pcm8kHz[i * 2] = (byte)(linearSample & 0xFF);
                pcm8kHz[i * 2 + 1] = (byte)(linearSample >> 8);
            }

            // Step 2: Resample from 8kHz to 16kHz
            byte[] pcm16kHz = ResamplePcmWithNAudio(pcm8kHz, 8000, 16000);
            return pcm16kHz;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to convert PCMU to 16-bit 16kHz PCM.");
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Processes a PCMU chunk to halve its volume by decoding to linear PCM, scaling, and re-encoding.
    /// The operation is purely per-sample: it decodes each mu-law (PCMU) byte to a linear PCM value, 
    /// applies a scalar multiplier to adjust amplitude, clamps if needed, and re-encodes to mu-law—without 
    /// any dependency on timing, duration, or frequency content. This holds regardless of whether the audio 
    /// is at 8 kHz (standard for G.711) or another rate, as long as the input is a valid sequence of mu-law samples.
    /// </summary>
    public static byte[] AdjustPcmuVolume(byte[] pcmuInput, float scaleFactor = 0.5f)
    {
        int sampleCount = pcmuInput.Length;
        byte[] output = new byte[sampleCount];

        for (int i = 0; i < sampleCount; i++)
        {
            // Decode mu-law sample to 16-bit linear PCM
            short linear = MuLawDecoder.MuLawToLinearSample(pcmuInput[i]);
            // Scale by scaleFactor
            int scaled = (int)(linear * scaleFactor);
            // Clamp to 16-bit range if necessary (though scaling should not overflow)
            if (scaled > short.MaxValue) scaled = short.MaxValue;
            if (scaled < short.MinValue) scaled = short.MinValue;
            // Re-encode to mu-law
            output[i] = MuLawEncoder.LinearToMuLawSample((short)scaled);
        }
        return output;
    }

    /// <summary>
    /// Converts 16-bit PCM audio (e.g., at 16kHz) to PCMU (G.711 μ-law) encoded audio at the specified output rate (default 8kHz for telephony).
    /// </summary>
    /// <param name="pcmAudio">16-bit mono PCM byte array (e.g., 16kHz)</param>
    /// <param name="inputSampleRate">Input sample rate (e.g., 16000)</param>
    /// <param name="outputSampleRate">Output sample rate for PCMU (default 8000)</param>
    /// <returns>PCMU-encoded byte array at outputSampleRate</returns>
    public static byte[] ConvertPcmToPcmu(byte[] pcmAudio, int inputSampleRate, int outputSampleRate = 8000)
    {
        try
        {
            // Step 1: Ensure even length
            if (pcmAudio.Length % 2 != 0)
            {
                Log.Warning($"Invalid input PCM length: {pcmAudio.Length} bytes (must be even for 16-bit), padding with 1 byte");
                byte[] paddedInput = new byte[pcmAudio.Length + 1];
                Array.Copy(pcmAudio, paddedInput, pcmAudio.Length);
                pcmAudio = paddedInput;
            }

            // Step 2: Resample to outputSampleRate if needed
            byte[] resampledPcm;
            if (inputSampleRate == outputSampleRate)
            {
                resampledPcm = pcmAudio;
            }
            else
            {
                resampledPcm = ResamplePcmWithNAudio(pcmAudio, inputSampleRate, outputSampleRate);
            }

            // Step 3: Encode 16-bit PCM samples to μ-law bytes
            int sampleCount = resampledPcm.Length / 2; // 16-bit samples
            byte[] pcmuOutput = new byte[sampleCount]; // 1 byte per μ-law sample

            for (int i = 0; i < sampleCount; i++)
            {
                // Read little-endian 16-bit sample
                short linearSample = (short)((resampledPcm[i * 2 + 1] << 8) | resampledPcm[i * 2]);
                // Encode to μ-law
                pcmuOutput[i] = MuLawEncoder.LinearToMuLawSample(linearSample);
            }

            return pcmuOutput;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Failed to convert PCM to PCMU.");
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// Appends the specified data to the original buffer, creating a new combined buffer.
    /// </summary>
    /// <param name="originalBuffer">The original byte array to which data will be appended.</param>
    /// <param name="dataToAppend">The byte array to append to the original buffer.</param>
    /// <returns>A new byte array containing the original buffer followed by the appended data.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="originalBuffer"/> or <paramref name="dataToAppend"/> is null.</exception>
    public static byte[] AppendBuffer(byte[] originalBuffer, byte[] dataToAppend)
    {
        ArgumentNullException.ThrowIfNull(originalBuffer);
        ArgumentNullException.ThrowIfNull(dataToAppend);

        byte[] extendedAudio = new byte[originalBuffer.Length + dataToAppend.Length];
        Buffer.BlockCopy(originalBuffer, 0, extendedAudio, 0, originalBuffer.Length);
        Buffer.BlockCopy(dataToAppend, 0, extendedAudio, originalBuffer.Length, dataToAppend.Length);
        return extendedAudio;
    }

    /// <summary>
    /// Generates mu-law silence audio for the specified duration in seconds.
    /// </summary>
    public static byte[] GeneratePcmuSilence(int durationSeconds, int sampleRate = 8000)
    {
        const byte silenceSample = 0xFF; // Mu-law silence
        int byteCount = sampleRate * durationSeconds;
        byte[] silence = new byte[byteCount];
        Array.Fill(silence, silenceSample);
        return silence;
    }

    /// <summary>
    /// Generates PCM 16kHz silence audio for the specified duration in seconds.
    /// </summary>
    public static byte[] GeneratePcm16kHzSilence(int durationSeconds, int sampleRate = 16000)
    {
        const int bytesPerSample = 2; // 16-bit
        int byteCount = sampleRate * durationSeconds * bytesPerSample;
        byte[] silence = new byte[byteCount]; // Zero-initialized for silence
        return silence;
    }
}