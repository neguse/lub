// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/Rect.hx と対)。
// Haxe 版は typedef (構造体リテラル) だが、C# には無いので
// public フィールド + 位置引数コンストラクタの素直な class にする。

/// <summary>アトラス内の矩形 (px、左上原点)。SpriteBatch の src 指定に使う。</summary>
public class Rect
{
    public int x;
    public int y;
    public int w;
    public int h;

    public Rect(int x, int y, int w, int h)
    {
        this.x = x;
        this.y = y;
        this.w = w;
        this.h = h;
    }
}
