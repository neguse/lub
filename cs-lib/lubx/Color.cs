// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/Color.hx と対)。
// Haxe 版は abstract + 構造体リテラルの暗黙変換だが、C# には無い機能なので
// static factory (rgb / hex) + public フィールドの素直な設計にする。
// メンバー名は Haxe 版 API と揃える (--no-naming-check でビルドされる)。
// a はデフォルト引数でなく nullable + ?? で受ける (tcs はデフォルト引数値を
// 呼び出し側に埋めないが、Lua の省略引数 = nil が null に落ちるのを利用)。

/// <summary>RGBA カラー。各成分 0..1。生成は Color.rgb / Color.hex で。</summary>
public class Color
{
    public double r;
    public double g;
    public double b;
    public double a;

    private Color(double r, double g, double b, double a)
    {
        this.r = r;
        this.g = g;
        this.b = b;
        this.a = a;
    }

    /// <summary>成分指定 (a 省略で 1.0)。</summary>
    public static Color rgb(double r, double g, double b, double? a = null)
    {
        return new Color(r, g, b, a ?? 1.0);
    }

    /// <summary>0xRRGGBB (a 省略で 1.0)。例: Color.hex(0xE85C5C)。</summary>
    public static Color hex(int rgb, double? a = null)
    {
        // tcs は bitwise 演算・整数除算未対応なので floor で分解する
        double r = System.Math.Floor(rgb / 65536.0) % 256 / 255.0;
        double g = System.Math.Floor(rgb / 256.0) % 256 / 255.0;
        double b = rgb % 256 / 255.0;
        return new Color(r, g, b, a ?? 1.0);
    }
}
