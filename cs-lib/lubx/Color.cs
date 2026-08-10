// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/Color.hx と対)。
// Haxe 版は abstract + 構造体リテラルの暗黙変換だが、C# には無い機能なので
// static factory (rgb / hex) + public フィールドの素直な設計にする。
// メンバー名は Haxe 版 API と揃える (--no-naming-check でビルドされる)。
// a はデフォルト引数でなく nullable + ?? で受ける (tcs はデフォルト引数値を
// 呼び出し側に埋めないが、Lua の省略引数 = nil が null に落ちるのを利用)。

/// <summary>RGBA カラー。各成分 0..1。生成は Color.rgb / Color.hex で。</summary>
public class Color
{
    public float r;
    public float g;
    public float b;
    public float a;

    private Color(float r, float g, float b, float a)
    {
        this.r = r;
        this.g = g;
        this.b = b;
        this.a = a;
    }

    /// <summary>成分指定 (a 省略で 1.0)。</summary>
    public static Color rgb(float r, float g, float b, float? a = null)
    {
        return new Color(r, g, b, a ?? 1.0f);
    }

    /// <summary>0xRRGGBB (a 省略で 1.0)。例: Color.hex(0xE85C5C)。</summary>
    public static Color hex(int rgb, float? a = null)
    {
        // tcs は bitwise 演算・整数除算未対応なので floor で分解する
        float r = (float)System.Math.Floor(rgb / 65536.0f) % 256 / 255.0f;
        float g = (float)System.Math.Floor(rgb / 256.0f) % 256 / 255.0f;
        float b = rgb % 256 / 255.0f;
        return new Color(r, g, b, a ?? 1.0f);
    }
}
