// 実装ライブラリ lubx の Rand。
// state は int (32bit wrap) で持つ。<< は int32 wrap がそのまま xorshift32 の
// 定義どおりで、論理シフト >>> は算術シフト >> と & マスク (17bit シフト後の
// 有効 15bit を 0x7FFF で残す) で再現する。golden はこの列に依存する。
// float / int は C# の予約語のため NextFloat / NextInt。

/// <summary>
/// 決定的な xorshift32 乱数。固定シードで hot reload / headless 検証でも
/// 再現可能 (Math.random は reload のたびに列が変わるので使わない)。
/// </summary>
using static Lub;

public class Rand
{
    private int state;

    public Rand(int? seed = null)
    {
        int s = seed ?? 0x12345678;
        state = s == 0 ? 0x12345678 : s;
    }

    /// <summary>[0, 1) の一様乱数。</summary>
    public float NextFloat()
    {
        state = state ^ (state << 13);
        state = state ^ ((state >> 17) & 0x7FFF);
        state = state ^ (state << 5);
        return (state & 0xffff) / 65536.0f;
    }

    /// <summary>[0, n) の整数。</summary>
    public int NextInt(int n)
    {
        return (int)System.Math.Floor(NextFloat() * n);
    }

    /// <summary>[min, max) の一様乱数。</summary>
    public float Range(float min, float max)
    {
        return min + NextFloat() * (max - min);
    }
}
