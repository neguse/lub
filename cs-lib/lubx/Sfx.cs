// 実装ライブラリ lubx の Sfx。
// 波形は List<float>.Add で積む (TinyC# の List は最初から Lua array table)。
// 切り捨ては値が非負なので Math.Floor、i % 16 == 0 は整数剰余を避けて
// (i & 15) == 0。
using System.Collections.Generic;
using static Lub;

/// <summary>
/// 効果音の即席合成。同じパラメータからは常に同じ波形を生成する (決定的) ので、
/// Audio.Snd の内容 dedupe と合わせて hot reload しても snd handle が安定する。
/// 波形は内部 cache し、呼ぶたびに key で宣言し直す (version が同じなら runtime
/// は波形を読まない) ので毎フレーム宣言的に呼んでよい。sweep された snd は
/// 次に呼んだとき cache の波形から作り直される。
/// </summary>
public static class Sfx
{
    public const int Rate = 44100;

    private static Dictionary<string, List<float>> cache = new Dictionary<string, List<float>>();

    /// <summary>矩形波 blip。freq0→freq1 へスイープしつつ指数減衰 (exp(-5u))。snd handle を返す。</summary>
    public static int Blip(float freq0, float freq1, float dur, float vol)
    {
        var key = "blip:" + freq0 + ":" + freq1 + ":" + dur + ":" + vol;
        if (cache.TryGetValue(key, out var cached))
        {
            return Audio.Snd(key, cached, 1, Rate, 1);
        }
        var n = (int)System.Math.Floor(dur * Rate);
        var samples = new List<float>();
        var phase = 0.0f;
        for (var i = 0; i < n; i++)
        {
            var u = (float)i / n;
            var freq = freq0 + (freq1 - freq0) * u;
            phase += freq / Rate;
            var env = (float)System.Math.Exp(-5.0f * u);
            samples.Add((phase % 1.0f < 0.5f ? 1.0f : -1.0f) * env * vol);
        }
        cache[key] = samples;
        return Audio.Snd(key, samples, 1, Rate, 1);
    }

    /// <summary>ノイズバースト。指数減衰 (exp(-4u))、16 sample ごとにホールド更新。snd handle を返す。</summary>
    public static int Noise(float dur, float vol, int? seed = null)
    {
        var s = seed ?? 0x12345678;
        var key = "noise:" + dur + ":" + vol + ":" + s;
        if (cache.TryGetValue(key, out var cached))
        {
            return Audio.Snd(key, cached, 1, Rate, 1);
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
            samples.Add(hold * (float)System.Math.Exp(-4.0f * u) * vol);
        }
        cache[key] = samples;
        return Audio.Snd(key, samples, 1, Rate, 1);
    }
}
