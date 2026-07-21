using System.IO;
using System.Media;

namespace DisplayProfileManager.Services;

/// <summary>
/// Tiny synthesized UI sounds (no asset files / NuGet). Soft and short.
/// </summary>
internal static class UiSound
{
    private static readonly object Gate = new();
    private static byte[]? _clickWav;
    private static byte[]? _openWav;
    private static bool _enabled = true;

    public static void SetEnabled(bool enabled) => _enabled = enabled;

    public static void Click() => Play(ref _clickWav, BuildClick);

    public static void Open() => Play(ref _openWav, BuildOpen);

    private static void Play(ref byte[]? cache, Func<byte[]> builder)
    {
        if (!_enabled) return;
        try
        {
            byte[] wav;
            lock (Gate)
            {
                cache ??= builder();
                wav = cache;
            }
            using var ms = new MemoryStream(wav, writable: false);
            using var player = new SoundPlayer(ms);
            player.Play(); // async, non-blocking
        }
        catch
        {
            // Audio device busy / missing — ignore
        }
    }

    private static byte[] BuildClick() =>
        RenderTone(ms: 42, freqHz: 620, volume: 0.11, softAttack: true);

    private static byte[] BuildOpen()
    {
        // Two soft ascending notes
        var a = RenderToneSamples(ms: 90, freqHz: 520, volume: 0.09, softAttack: true);
        var b = RenderToneSamples(ms: 120, freqHz: 780, volume: 0.08, softAttack: true);
        var gap = new short[800]; // ~18ms silence @ 44.1k
        var mixed = new short[a.Length + gap.Length + b.Length];
        Array.Copy(a, 0, mixed, 0, a.Length);
        Array.Copy(b, 0, mixed, a.Length + gap.Length, b.Length);
        return WrapWav(mixed);
    }

    private static byte[] RenderTone(int ms, double freqHz, double volume, bool softAttack) =>
        WrapWav(RenderToneSamples(ms, freqHz, volume, softAttack));

    private static short[] RenderToneSamples(int ms, double freqHz, double volume, bool softAttack)
    {
        const int sampleRate = 44100;
        int n = Math.Max(1, sampleRate * ms / 1000);
        var samples = new short[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)sampleRate;
            double env = 1.0;
            if (softAttack)
            {
                double attack = Math.Min(1.0, i / (sampleRate * 0.008));
                double release = Math.Min(1.0, (n - i) / (sampleRate * 0.02));
                env = attack * release;
            }
            double s = Math.Sin(2 * Math.PI * freqHz * t) * volume * env;
            samples[i] = (short)Math.Clamp((int)(s * short.MaxValue), short.MinValue, short.MaxValue);
        }
        return samples;
    }

    private static byte[] WrapWav(short[] samples)
    {
        const int sampleRate = 44100;
        int dataBytes = samples.Length * 2;
        using var ms = new MemoryStream(44 + dataBytes);
        using var bw = new BinaryWriter(ms);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataBytes);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)1); // mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataBytes);
        foreach (var s in samples)
            bw.Write(s);
        return ms.ToArray();
    }
}
