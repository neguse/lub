// 実装ライブラリ lubx の Rand。
// xorshift32 の列を 32bit 整数の列に固定する (golden 互換) ため、state を
// 非負の 32bit 値 [0, 2^32) に保つ: << の後は & 0xFFFFFFFF で明示マスクし、
// >> は state が 32bit 非負なので論理シフトと一致する (マスク不要)。
// float / int は C# の予約語のため nextFloat / nextInt。

/// <summary>
/// 決定的な xorshift32 乱数。固定シードで hot reload / headless 検証でも
/// 再現可能 (Math.random は reload のたびに列が変わるので使わない)。
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

    /// <summary>[0, 1) の一様乱数。</summary>
    public double NextFloat()
    {
        state = (state ^ (state << 13)) & 0xFFFFFFFF;
        state = state ^ (state >> 17);
        state = (state ^ (state << 5)) & 0xFFFFFFFF;
        return (state & 0xffff) / 65536.0;
    }

    /// <summary>[0, n) の整数。</summary>
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
