// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/Rand.hx と対)。
// Lua 整数は 64bit なので、Haxe (32bit Int) と乱数列がビット一致するよう
// state を非負の 32bit 値 [0, 2^32) に保つ: << の後は & 0xFFFFFFFF で明示
// マスクし、>> は state が 32bit 非負なので Haxe の >>> (論理シフト) と一致
// する (マスク不要)。float / int は C# の予約語のため nextFloat / nextInt に
// 改名する (それ以外の API 名は Haxe 版と同名)。

/// <summary>
/// 決定的な xorshift32 乱数。固定シードで hot reload / headless 検証でも
/// 再現可能 (Math.random は reload のたびに列が変わるので使わない)。
/// 同一シードで Haxe 版と同じ乱数列を生成する。
/// </summary>
using static Lub;

public class Rand
{
    private long state;

    public Rand(int? seed = null)
    {
        long s = seed ?? 0x12345678;
        state = (s == 0 ? 0x12345678 : s) & 0xFFFFFFFF;
    }

    /// <summary>[0, 1) の一様乱数 (Haxe 版の float())。</summary>
    public double NextFloat()
    {
        state = (state ^ (state << 13)) & 0xFFFFFFFF;
        state = state ^ (state >> 17);
        state = (state ^ (state << 5)) & 0xFFFFFFFF;
        return (state & 0xffff) / 65536.0;
    }

    /// <summary>[0, n) の整数 (Haxe 版の int())。</summary>
    public int NextInt(int n)
    {
        return (int)System.Math.Floor(NextFloat() * n);
    }

    /// <summary>[min, max) の一様乱数。</summary>
    public double Range(double min, double max)
    {
        return min + NextFloat() * (max - min);
    }
}
