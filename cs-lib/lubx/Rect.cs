// 実装ライブラリ lubx の Rect。
// public フィールド + 位置引数コンストラクタの素直な class。

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
