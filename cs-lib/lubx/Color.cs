// 実装ライブラリ lubx の Color。
// static factory (rgb / hex) + public フィールドの素直な設計。
// a はデフォルト引数でなく nullable + ?? で受ける (tcs はデフォルト引数値を
// 呼び出し側に埋めないが、Lua の省略引数 = nil が null に落ちるのを利用)。

/// <summary>RGBA カラー。各成分 0..1。生成は Color.rgb / Color.hex で。</summary>
using static Lub;

public class Color
{
    public double R;
    public double G;
    public double B;
    public double A;

    private Color(double r, double g, double b, double a)
    {
        this.R = r;
        this.G = g;
        this.B = b;
        this.A = a;
    }

    /// <summary>成分指定 (a 省略で 1.0)。</summary>
    public static Color Rgb(double r, double g, double b, double? a = null)
    {
        return new Color(r, g, b, a ?? 1.0);
    }

    /// <summary>0xRRGGBB (a 省略で 1.0)。例: Color.hex(0xE85C5C)。</summary>
    public static Color Hex(int rgb, double? a = null)
    {
        // tcs は bitwise 演算・整数除算未対応なので floor で分解する
        double r = System.Math.Floor(rgb / 65536.0) % 256 / 255.0;
        double g = System.Math.Floor(rgb / 256.0) % 256 / 255.0;
        double b = rgb % 256 / 255.0;
        return new Color(r, g, b, a ?? 1.0);
    }
}
