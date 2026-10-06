namespace LoupixDeck.Plugin.Audio;

/// <summary>
/// A short beep written once as a WAV file, so the test sound goes through the same
/// <see cref="IAudioService.PlayFile"/> path as every other sound — WASAPI on Windows, paplay or
/// mpv on Linux — and a working test proves that path works too.
/// </summary>
internal static class TestTone
{
    private const int SampleRate = 48_000;
    private const double Frequency = 880;
    private const double DurationSeconds = 0.4;

    // Fade in and out so the tone does not start or end with a click.
    private const double FadeSeconds = 0.02;

    private static readonly Lock WriteLock = new();

    /// <summary>The tone file in the temp folder, written on first use (and again if it was cleaned up).</summary>
    public static string EnsureFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "loupixdeck-audio-test-tone.wav");
        lock (WriteLock)
        {
            if (!File.Exists(path)) File.WriteAllBytes(path, BuildWav());
        }
        return path;
    }

    /// <summary>16-bit mono PCM.</summary>
    private static byte[] BuildWav()
    {
        int samples = (int)(SampleRate * DurationSeconds);
        int fadeSamples = (int)(SampleRate * FadeSeconds);
        int dataBytes = samples * sizeof(short);

        using MemoryStream stream = new(44 + dataBytes);
        using BinaryWriter writer = new(stream);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);                          // fmt chunk size
        writer.Write((short)1);                    // PCM
        writer.Write((short)1);                    // mono
        writer.Write(SampleRate);
        writer.Write(SampleRate * sizeof(short));  // byte rate
        writer.Write((short)sizeof(short));        // block align
        writer.Write((short)16);                   // bits per sample
        writer.Write("data"u8);
        writer.Write(dataBytes);

        for (int i = 0; i < samples; i++)
        {
            double envelope = Math.Min(1.0, Math.Min(i, samples - 1 - i) / (double)fadeSamples);
            double value = Math.Sin(2 * Math.PI * Frequency * i / SampleRate) * envelope * 0.8;
            writer.Write((short)(value * short.MaxValue));
        }

        writer.Flush();
        return stream.ToArray();
    }
}
