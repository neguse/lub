// 実装ライブラリ lubx の Rect。
// public フィールド + 位置引数コンストラクタの素直な class。

using Lub;

namespace Lubx;

/// <summary>矩形 (px、左上原点)。
/// SpriteBatch の src 指定 (アトラスの中) と Renderer3d.Viewport (画面の中) に使う。
/// </summary>
public readonly struct Rect
{
    public readonly int X;
    public readonly int Y;
    public readonly int W;
    public readonly int H;

    public Rect(int x, int y, int w, int h)
    {
        this.X = x;
        this.Y = y;
        this.W = w;
        this.H = h;
    }
}
