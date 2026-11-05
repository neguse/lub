// 実装ライブラリ lubx の Rect。
// public フィールド + 位置引数コンストラクタの素直な class。

using Lub;

namespace Lubx;

/// <summary>矩形 (px、左上原点)。
/// SpriteBatch の src 指定 (アトラスの中) と Renderer3d.Viewport (画面の中) に使う。
/// </summary>
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
