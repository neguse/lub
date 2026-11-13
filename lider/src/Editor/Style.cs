// エディタの色、余白、font の id。
// 色は Lite XL の既定の色 (colors/default.lua) に、lider の色 (地は夜の藍、字に暖色を使わない) を重ねたもの。
// 色は RGBA を 32 bit に詰めた数 (R が上の 8 bit)。
using System.Collections.Generic;
using TinySystem.Text;

namespace LiderEditor;

public static class Style
{
    public const float PaddingX = 14;
    public const float PaddingY = 7;
    public const float DividerSize = 1;
    public const float ScrollbarSize = 4;
    public const float CaretWidth = 2;
    // tab と字下げの桁
    public const int TabSize = 4;
    // wheel の 1 notch で動く px
    public const float WheelScroll = 50;

    // font の id。lider は自分の等幅の font で描くので、名前は見分けにだけ使う
    public const int Font = 1;
    public const int BigFont = 2;
    public const int IconFont = 3;
    public const int IconBigFont = 4;
    public const int CodeFont = 5;

    public static string FontName(int id)
    {
        if (id == Font || id == BigFont)
            return "FiraSans-Regular.ttf";
        if (id == IconFont || id == IconBigFont)
            return "icons.ttf";
        if (id == CodeFont)
            return "JetBrainsMono-Regular.ttf";
        return "";
    }

    // 面には下地を塗らない (奥の層と実行した行の光が透けて見える)
    public static readonly int Background = Rgba(0, 0, 0, 0);
    public static readonly int Background3 = Hex("#252529");
    public static readonly int Text = Hex("#a9b1d0");
    public static readonly int Caret = Hex("#8fa8ff");
    public static readonly int Accent = Hex("#e8ecf6");
    public static readonly int Dim = Hex("#5c6480");
    public static readonly int Divider = Hex("#1a1f2e");
    public static readonly int Selection = Hex("#2f3f6b");
    public static readonly int LineNumber = Hex("#3f4660");
    public static readonly int LineNumber2 = Hex("#8a93b2");
    // 今の行の帯は薄くする
    public static readonly int LineHighlight = Rgba(255, 255, 255, 12);
    public static readonly int Scrollbar = Hex("#3a4260");
    // 確認の帯は赤く塗らず、地の色に乗せる
    public static readonly int Nagbar = Hex("#1a2142");
    public static readonly int NagbarText = Hex("#e8ecf6");

    // token の種類 (TokenType) ごとの色。琥珀は実行と値に取っておくので、字には暖色を使わない
    static List<int>? syntax;

    public static int Syntax(int type)
    {
        if (syntax == null)
            syntax = new List<int>
            {
                Hex("#ccd4ee"), // normal
                Hex("#ccd4ee"), // symbol
                Hex("#606c92"), // comment
                Hex("#a99cf5"), // keyword
                Hex("#6fc7e8"), // keyword2
                Hex("#f09ac0"), // number
                Hex("#f09ac0"), // literal
                Hex("#8fd0a8"), // string
                Hex("#93a9d6"), // operator
                Hex("#8fb4ff"), // function
            };
        return syntax[type];
    }

    public static int Rgba(int r, int g, int b, int a)
    {
        return (r << 24) | (g << 16) | (b << 8) | a;
    }

    // "#rrggbb" か "#rrggbbaa"
    public static int Hex(string s)
    {
        var u = new Utf8Bytes(s);
        var r = Digit(u[1]) * 16 + Digit(u[2]);
        var g = Digit(u[3]) * 16 + Digit(u[4]);
        var b = Digit(u[5]) * 16 + Digit(u[6]);
        var a = u.Length >= 9 ? Digit(u[7]) * 16 + Digit(u[8]) : 255;
        return Rgba(r, g, b, a);
    }

    static int Digit(int c)
    {
        if (c >= '0' && c <= '9')
            return c - '0';
        if (c >= 'a' && c <= 'f')
            return c - 'a' + 10;
        if (c >= 'A' && c <= 'F')
            return c - 'A' + 10;
        return 0;
    }
}
