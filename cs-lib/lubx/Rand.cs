// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/Rand.hx と対)。
// state は int (32bit wrap) で持ち、Haxe (32bit Int) と乱数列がビット一致
// する: << は int32 wrap がそのまま Haxe と同じ。Haxe の >>> (論理シフト) は
// C# の算術シフト >> と & マスクで再現する (17bit シフト後の有効 15bit を
// 0x7FFF で残す)。float / int は C# の予約語のため nextFloat / nextInt に
// 改名する (それ以外の API 名は Haxe 版と同名)。

/// <summary>
/// 決定的な xorshift32 乱数。固定シードで hot reload / headless 検証でも
/// 再現可能 (Math.random は reload のたびに列が変わるので使わない)。
/// 同一シードで Haxe 版と同じ乱数列を生成する。
/// </summary>
public class Rand
{
    private int state;

    public Rand(int? seed = null)
    {
        int s = seed ?? 0x12345678;
        state = s == 0 ? 0x12345678 : s;
    }

    /// <summary>[0, 1) の一様乱数 (Haxe 版の float())。</summary>
    public float nextFloat()
    {
        state = state ^ (state << 13);
        state = state ^ ((state >> 17) & 0x7FFF);
        state = state ^ (state << 5);
        return (state & 0xffff) / 65536.0f;
    }

    /// <summary>[0, n) の整数 (Haxe 版の int())。</summary>
    public int nextInt(int n)
    {
        return (int)System.Math.Floor(nextFloat() * n);
    }

    /// <summary>[min, max) の一様乱数。</summary>
    public float range(float min, float max)
    {
        return min + nextFloat() * (max - min);
    }
}
