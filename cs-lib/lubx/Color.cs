// 実装ライブラリ lubx の Color。
// static factory (rgb / hex) + 読み取り専用の public フィールド。
// 毎 frame 大量に作るので、確保の要らない値型 (readonly struct。Lua でも copy の地点で table を作らない) にする。
// a はデフォルト引数でなく nullable + ?? で受ける (tcs はデフォルト引数値を呼び出し側に埋めないが、Lua の省略引数 = nil が null に落ちるのを利用)。

using Lub;

namespace Lubx;

/// <summary>RGBA カラー (値型)。
/// 各成分 0..1。
/// 生成は Color.rgb / Color.hex で。
/// </summary>
public readonly struct Color
{
    public readonly float R;
    public readonly float G;
    public readonly float B;
    public readonly float A;

    private Color(float r, float g, float b, float a)
    {
        this.R = r;
        this.G = g;
        this.B = b;
        this.A = a;
    }

    /// <summary>成分指定 (a 省略で 1.0)。
    /// </summary>
    public static Color Rgb(float r, float g, float b, float? a = null)
    {
        return new Color(r, g, b, a ?? 1.0f);
    }

    /// <summary>0xRRGGBB (a 省略で 1.0)。
    /// 例: Color.hex(0xE85C5C)。
    /// </summary>
    public static Color Hex(int rgb, float? a = null)
    {
        // tcs は bitwise 演算・整数除算未対応なので floor で分解する
        float r = (float)System.Math.Floor(rgb / 65536.0f) % 256 / 255.0f;
        float g = (float)System.Math.Floor(rgb / 256.0f) % 256 / 255.0f;
        float b = rgb % 256 / 255.0f;
        return new Color(r, g, b, a ?? 1.0f);
    }
}
