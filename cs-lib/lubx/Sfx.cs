// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/Sfx.hx と対)。
// Haxe 版の lua.Table.create + 1-based 直接代入は List<double>.Add で置き
// 換える (TinyC# の List は最初から Lua array table)。Std.int は値が非負
// なので Math.Floor で代替、i % 16 == 0 は整数剰余を避けて (i & 15) == 0。
using System.Collections.Generic;
using static Lub;

/// <summary>
/// 効果音の即席合成。同じパラメータからは常に同じ波形を生成する (決定的) ので、
/// Audio.pcm の内容 dedupe と合わせて hot reload しても snd handle が安定する。
/// 結果は内部 cache するので毎フレーム宣言的に呼んでよい。
/// </summary>
public static class Sfx
{
    public const int Rate = 44100;

    private static Dictionary<string, int> cache = new Dictionary<string, int>();

    /// <summary>矩形波 blip。freq0→freq1 へスイープしつつ指数減衰 (exp(-5u))。snd handle を返す。</summary>
    public static int Blip(double freq0, double freq1, double dur, double vol)
    {
        var key = "blip:" + freq0 + ":" + freq1 + ":" + dur + ":" + vol;
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var n = (int)System.Math.Floor(dur * Rate);
        var samples = new List<double>();
        var phase = 0.0;
        for (var i = 0; i < n; i++)
        {
            var u = (double)i / n;
            var freq = freq0 + (freq1 - freq0) * u;
            phase += freq / Rate;
            var env = System.Math.Exp(-5.0 * u);
            samples.Add((phase % 1.0 < 0.5 ? 1.0 : -1.0) * env * vol);
        }
        var snd = Audio.Pcm(samples, 1, Rate);
        cache[key] = snd;
        return snd;
    }

    /// <summary>ノイズバースト。指数減衰 (exp(-4u))、16 sample ごとにホールド更新。snd handle を返す。</summary>
    public static int Noise(double dur, double vol, int? seed = null)
    {
        var s = seed ?? 0x12345678;
        var key = "noise:" + dur + ":" + vol + ":" + s;
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var n = (int)System.Math.Floor(dur * Rate);
        var samples = new List<double>();
        var r = new Rand(s);
        var hold = 0.0;
        for (var i = 0; i < n; i++)
        {
            if ((i & 15) == 0)
            {
                hold = r.NextFloat() * 2.0 - 1.0;
            }
            var u = (double)i / n;
            samples.Add(hold * System.Math.Exp(-4.0 * u) * vol);
        }
        var snd = Audio.Pcm(samples, 1, Rate);
        cache[key] = snd;
        return snd;
    }
}
