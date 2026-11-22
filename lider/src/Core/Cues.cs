// Feedback sounds for actions. There are no sound assets; waveforms are synthesized at startup.
// Volume follows the impact on the user, not the size of the action (fixed > applied > error > save > move > keystroke).
// Waveforms are normalized to a peak amplitude of 1 and played at the Play volume (1 is the loudest sound) multiplied by Loudest. They must not stand out over the game's sounds.
// Repeated sounds vary in pitch, and all of them have a strong attack and a short tail.
using System;
using System.Collections.Generic;
using Lub;

namespace LiderCore;

public static class Cues
{
    const int Rate = 44100;
    public const string Key = "key";
    public const string Save = "save";
    public const string Land = "land";
    public const string Error = "error";
    public const string Fixed = "fixed";
    public const string Move = "move";

    // Peak amplitude of a sound played at volume 1 (game sounds peak at about 0.1 to 0.3)
    public const float Loudest = 0.1f;

    public static bool Muted;
    // Name of the last sound played (unchanged when Muted)
    public static string Last = "";
    static readonly Dictionary<string, List<float>> waves = new Dictionary<string, List<float>>();
    // Peak amplitude of each waveform
    static readonly Dictionary<string, float> wavePeaks = new Dictionary<string, float>();
    // Per sound name, the largest peak amplitude played
    public static readonly Dictionary<string, float> Peaks = new Dictionary<string, float>();
    static readonly Dictionary<string, int> handles = new Dictionary<string, int>();
    static int seed = 54321;
    // Bump when the waveforms change (lub does not reload a sound with the same key and version even when the VM is recreated)
    const int WaveVersion = 2;

    // Called every frame while lider is shown (sounds are discarded when the declaration lapses)
    public static void Declare()
    {
        if (waves.Count == 0)
            Build();
        foreach (var pair in waves)
        {
            // With the same version the waveform is not read. If it was discarded, it is passed again
            var snd = Audio.Snd("lider_cue_" + pair.Key, null, 1, Rate, WaveVersion) ?? Audio.Snd("lider_cue_" + pair.Key, pair.Value, 1, Rate, WaveVersion);
            if (snd != null)
                handles[pair.Key] = snd.Value;
        }
    }

    public static void Play(string name, float volume, float pitch = 1)
    {
        if (Muted)
            return;
        Last = name;
        if (!handles.TryGetValue(name, out var snd))
            return;
        Audio.Play(snd, new PlayOpts { Volume = volume * Loudest, Pitch = pitch });
        var peak = wavePeaks[name] * volume * Loudest;
        if (!Peaks.TryGetValue(name, out var loudest) || peak > loudest)
            Peaks[name] = peak;
    }

    // Pseudo-random number from 0 to 1
    public static float Random()
    {
        seed = seed * 1664525 + 1013904223;
        return (float)((seed >> 8) & 0xffff) / 65535.0f;
    }

    static void Build()
    {
        waves[Key] = Wave(0.04f, t => (Random() * 2 - 1) * (float)Math.Exp(-t * 130) * 0.7f + (float)Math.Sin(t * 1200) * (float)Math.Exp(-t * 70) * 0.4f);
        waves[Save] = Wave(0.09f, t => (float)Math.Sin(t * t * 16000 + t * 1900) * (float)Math.Exp(-t * 28) * 0.8f);
        waves[Land] = Wave(0.26f, t => ((float)Math.Sin(t * 5530) + 0.6f * (float)Math.Sin(t * 8290)) * (float)Math.Exp(-t * 15) * 0.6f
            + (t < 0.012f ? (Random() * 2 - 1) * 0.8f : 0));
        waves[Error] = Wave(0.17f, t => (t < 0.06f || (t > 0.09f && t < 0.15f) ? ((float)Math.Sin(t * 820) > 0 ? 0.7f : -0.7f) : 0) * (float)Math.Exp(-t * 6));
        waves[Fixed] = Wave(0.62f, Fanfare);
        waves[Move] = Wave(0.07f, t => (float)Math.Sin(t * t * 19000 + t * 3100) * (float)Math.Exp(-t * 45));
        foreach (var pair in waves)
        {
            var data = pair.Value;
            var peak = 0.0f;
            foreach (var v in data)
                peak = Math.Max(peak, Math.Abs(v));
            var top = 0.0f;
            for (var i = 0; i < data.Count; i++)
            {
                data[i] /= peak;
                top = Math.Max(top, Math.Abs(data[i]));
            }
            wavePeaks[pair.Key] = top;
        }
    }

    // 4 notes rising upward
    static float Fanfare(float t)
    {
        var notes = new List<float> { 523, 659, 784, 1047 };
        var v = 0.0f;
        for (var i = 0; i < notes.Count; i++)
        {
            var at = t - i * 0.075f;
            if (at >= 0)
                v += (float)Math.Sin(at * notes[i] * 6.2832f) * (float)Math.Exp(-at * (i == notes.Count - 1 ? 6 : 14));
        }
        return v * 0.45f;
    }

    static List<float> Wave(float seconds, Func<float, float> at)
    {
        var data = new List<float>();
        var n = (int)(seconds * Rate);
        for (var i = 0; i < n; i++)
        {
            var v = at((float)i / Rate);
            // Avoids a click at the end
            data.Add(v * Math.Min(1, (n - i) / 200.0f));
        }
        return data;
    }
}
