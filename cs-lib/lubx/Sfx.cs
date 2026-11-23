// Sfx of the implementation library lubx.
// Waveforms are accumulated with List<float>.Add.
// Truncation uses Math.Floor since the values are non-negative, and i % 16 == 0 is written as (i & 15) == 0 to avoid integer remainder.
using System;
using System.Collections.Generic;
using Lub;

namespace Lubx;

/// <summary> Quick sound effect synthesis.
/// The same parameters always produce the same waveform (deterministic), so together with the content dedupe of Audio.Snd the snd handle stays stable across hot reload.
/// Waveforms are cached internally and re-declared by key on every call (the runtime does not read the waveform if version is the same), so it can be called declaratively every frame.
/// A swept snd is recreated from the cached waveform on the next call.
/// </summary>
public static class Sfx
{
    public const int Rate = 44100;

    private static Dictionary<string, List<float>> cache = new Dictionary<string, List<float>>();
    private static Dictionary<string, int> synthVersions = new Dictionary<string, int>();

    /// <summary>Square-wave blip.
    /// Sweeps freq0→freq1 with exponential decay (exp(-5u)).
    /// Returns a snd handle.
    /// </summary>
    public static int Blip(float freq0, float freq1, float dur, float vol)
    {
        var key = "blip:" + freq0 + ":" + freq1 + ":" + dur + ":" + vol;
        if (cache.TryGetValue(key, out var cached))
        {
            return Audio.Snd(key, cached, 1, Rate, 1) ?? 0;
        }
        var n = (int)System.Math.Floor(dur * Rate);
        var samples = new List<float>();
        var phase = 0.0f;
        for (var i = 0; i < n; i++)
        {
            var u = (float)i / n;
            var freq = freq0 + (freq1 - freq0) * u;
            phase += freq / Rate;
            var env = (float)Math.Exp(-5.0f * u);
            samples.Add((phase % 1.0f < 0.5f ? 1.0f : -1.0f) * env * vol);
        }
        cache[key] = samples;
        return Audio.Snd(key, samples, 1, Rate, 1) ?? 0;
    }

    /// <summary>Noise burst.
    /// Exponential decay (exp(-4u)), with the held value updated every 16 samples.
    /// Returns a snd handle.
    /// </summary>
    public static int Noise(float dur, float vol, int? seed = null)
    {
        var s = seed ?? 0x12345678;
        var key = "noise:" + dur + ":" + vol + ":" + s;
        if (cache.TryGetValue(key, out var cached))
        {
            return Audio.Snd(key, cached, 1, Rate, 1) ?? 0;
        }
        var n = (int)System.Math.Floor(dur * Rate);
        var samples = new List<float>();
        var r = new Rand(s);
        var hold = 0.0f;
        for (var i = 0; i < n; i++)
        {
            if ((i & 15) == 0)
            {
                hold = r.NextFloat() * 2.0f - 1.0f;
            }
            var u = (float)i / n;
            samples.Add(hold * (float)Math.Exp(-4.0f * u) * vol);
        }
        cache[key] = samples;
        return Audio.Snd(key, samples, 1, Rate, 1) ?? 0;
    }

    /// <summary>Framework for synthesizing an arbitrary waveform.
    /// sample(t, u) receives t seconds (the sample position) and progress u (0..1) and returns 1 sample, which is clamped to -1..1.
    /// State such as LP filters is kept by the caller in variables captured by the closure (sample is called once per sample, in order from the start).
    /// Cached by key and version; with the same version, sample is not called.
    /// Raise version when the waveform formula changes.
    /// With fadeSamples, the last that many samples (capped at the total number of samples in dur) are lowered linearly toward a final 0 (prevents clicks when the waveform does not end at 0).
    /// When omitted, nothing is lowered.
    /// When fadeSamples changes, raise version just as for a waveform formula change.
    /// Call it every frame to keep declaring it.
    /// Returns a snd handle.
    /// </summary>
    public static int Synth(string key, float dur, int version, Func<float, float, float> sample, int? fadeSamples = null)
    {
        if (synthVersions.TryGetValue(key, out var cachedVersion) && cachedVersion == version
            && cache.TryGetValue(key, out var cached))
        {
            return Audio.Snd(key, cached, 1, Rate, version) ?? 0;
        }
        var n = (int)System.Math.Floor(dur * Rate);
        var samples = new List<float>();
        var fade = System.Math.Min(fadeSamples ?? 0, n);
        for (var i = 0; i < n; i++)
        {
            var s = sample((float)i / Rate, (float)i / n);
            samples.Add(System.Math.Max(-1.0f, System.Math.Min(1.0f, s)) * FadeGain(i, n, fade));
        }
        cache[key] = samples;
        synthVersions[key] = version;
        return Audio.Snd(key, samples, 1, Rate, version) ?? 0;
    }

    /// <summary>Tail fade coefficient for the i-th of n samples.
    /// It falls linearly from 1 to 0 over the last fade samples, and the last sample becomes 0.
    /// Always 1 if fade is 0 or less.
    /// </summary>
    private static float FadeGain(int i, int n, int fade)
    {
        if (fade <= 0 || i < n - fade)
        {
            return 1.0f;
        }
        return (float)(n - 1 - i) / fade;
    }
}
