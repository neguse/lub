// Editor colors, padding, and font ids.
// Colors are Lite XL's default colors (colors/default.lua) with lider's colors layered on top (a night indigo background, no warm colors for text).
// Colors are numbers with RGBA packed into 32 bits (R in the top 8 bits).
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
    // Columns for tabs and indentation
    public const int TabSize = 4;
    // px moved per wheel notch
    public const float WheelScroll = 50;

    // Font ids. lider draws with its own monospace font, so the names are used only to tell them apart
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

    // No background is painted on the surface (the back layers and the glow of executed lines show through)
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
    // The current line band is faint
    public static readonly int LineHighlight = Rgba(255, 255, 255, 12);
    public static readonly int Scrollbar = Hex("#3a4260");
    // The confirmation strip is not painted red; it sits on the background color
    public static readonly int Nagbar = Hex("#1a2142");
    public static readonly int NagbarText = Hex("#e8ecf6");

    // Colors per token kind (TokenType). Amber is reserved for execution and values, so text uses no warm colors
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

    // "#rrggbb" or "#rrggbbaa"
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
