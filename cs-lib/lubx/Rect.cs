// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/Rect.hx と対)。
// Haxe 版は typedef (構造体リテラル) だが、C# には無いので
// public フィールド + 位置引数コンストラクタの素直な class にする。

/// <summary>アトラス内の矩形 (px、左上原点)。SpriteBatch の src 指定に使う。</summary>
using static Lub;

public class Rect
{
    public int X;
    public int Y;
    public int W;
    public int H;

    public Rect(int x, int y, int w, int h)
    {
        this.X = x;
        this.Y = y;
        this.W = w;
        this.H = h;
    }
}
