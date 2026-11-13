// 操作の手応えの音。素材は持たず、起動したときに波形を合成する。
// 音量は操作の大きさでなく、使う人への影響の順にする (直った > 反映 > error > 保存 > 移動 > 打鍵)。
// 波形は振幅の山を 1 に揃え、Play の音量 (1 が一番大きい音) に Loudest を掛けて鳴らす。ゲームの音より目立たせない。
// 繰り返す音は高さをばらつかせ、どれも出だしを強く、残響を短くする。
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

    // 音量 1 で鳴らした音の振幅の山 (ゲームの音の山はおよそ 0.1〜0.3)
    public const float Loudest = 0.1f;

    public static bool Muted;
    // 最後に鳴らした音の名前 (Muted なら変わらない)
    public static string Last = "";
    static readonly Dictionary<string, List<float>> waves = new Dictionary<string, List<float>>();
    // 波形の振幅の山
    static readonly Dictionary<string, float> wavePeaks = new Dictionary<string, float>();
    // 鳴らした音の名前ごとの、鳴らした振幅の山の一番大きいもの
    public static readonly Dictionary<string, float> Peaks = new Dictionary<string, float>();
    static readonly Dictionary<string, int> handles = new Dictionary<string, int>();
    static int seed = 54321;
    // 波形を変えたら上げる (lub は VM を作り直しても同じ key と version の音を読み直さない)
    const int WaveVersion = 2;

    // lider を出している間、毎 frame 呼ぶ (宣言が途切れると捨てられる)
    public static void Declare()
    {
        if (waves.Count == 0)
            Build();
        foreach (var pair in waves)
        {
            // 同じ version なら波形は読まれない。捨てられた後なら渡し直す
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

    // 0 から 1 の擬似乱数
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

    // 上へ駆け上がる 4 つの音
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
            // 終わりでぷつっと鳴らない
            data.Add(v * Math.Min(1, (n - i) / 200.0f));
        }
        return data;
    }
}
