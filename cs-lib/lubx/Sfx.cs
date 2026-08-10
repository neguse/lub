// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/Sfx.hx と対)。
// Haxe 版の lua.Table.create + 1-based 直接代入は List<float>.Add で置き
// 換える (TinyC# の List は最初から Lua array table)。Std.int は値が非負
// なので Math.Floor で代替、i % 16 == 0 は整数剰余を避けて (i & 15) == 0。
using System.Collections.Generic;

/// <summary>
/// 効果音の即席合成。同じパラメータからは常に同じ波形を生成する (決定的) ので、
/// Audio.pcm の内容 dedupe と合わせて hot reload しても snd handle が安定する。
/// 結果は内部 cache するので毎フレーム宣言的に呼んでよい。
/// </summary>
public static class Sfx
{
    public const int RATE = 44100;

    private static Dictionary<string, int> cache = new Dictionary<string, int>();

    /// <summary>矩形波 blip。freq0→freq1 へスイープしつつ指数減衰 (exp(-5u))。snd handle を返す。</summary>
    public static int blip(float freq0, float freq1, float dur, float vol)
    {
        var key = "blip:" + freq0 + ":" + freq1 + ":" + dur + ":" + vol;
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var n = (int)System.Math.Floor(dur * RATE);
        var samples = new List<float>();
        var phase = 0.0f;
        for (var i = 0; i < n; i++)
        {
            var u = (float)i / n;
            var freq = freq0 + (freq1 - freq0) * u;
            phase += freq / RATE;
            var env = (float)System.Math.Exp(-5.0f * u);
            samples.Add((phase % 1.0f < 0.5f ? 1.0f : -1.0f) * env * vol);
        }
        var snd = Audio.audio_pcm(samples, 1, RATE);
        cache[key] = snd;
        return snd;
    }

    /// <summary>ノイズバースト。指数減衰 (exp(-4u))、16 sample ごとにホールド更新。snd handle を返す。</summary>
    public static int noise(float dur, float vol, int? seed = null)
    {
        var s = seed ?? 0x12345678;
        var key = "noise:" + dur + ":" + vol + ":" + s;
        if (cache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        var n = (int)System.Math.Floor(dur * RATE);
        var samples = new List<float>();
        var r = new Rand(s);
        var hold = 0.0f;
        for (var i = 0; i < n; i++)
        {
            if ((i & 15) == 0)
            {
                hold = r.nextFloat() * 2.0f - 1.0f;
            }
            var u = (float)i / n;
            samples.Add(hold * (float)System.Math.Exp(-4.0f * u) * vol);
        }
        var snd = Audio.audio_pcm(samples, 1, RATE);
        cache[key] = snd;
        return snd;
    }
}
