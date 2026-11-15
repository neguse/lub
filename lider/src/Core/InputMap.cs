// 入力の位置を、ゲームの texture の画素に直す。
// ゲームの絵のウィンドウは、どのスクリーン (画面に貼った平面、空間の円筒) にも置けるので、位置は「絵の中の位置 (0 から 1)」で受け取る。
// 保存した版と編集中の版を並べて入力を揃えるとき、どちらのゲームも、マウスのあるウィンドウの絵の中の同じ位置を受け取る。
using Lub;

namespace LiderCore;

public class InputMap
{
    // texture の画素の数
    public int W = 1;
    public int H = 1;
    // 指している、絵の中の位置 (左上が 0,0、右下が 1,1)
    float u;
    float v;
    // 画面の座標の 1 に対する、texture の画素の数 (マウスの移動の量を直す)
    float scale = 1;

    // w と h は texture の画素の数
    public void Set(int w, int h)
    {
        W = w;
        H = h;
    }

    // uv は指している絵の中の位置、screenW は指しているウィンドウの絵の、画面の上の幅 (画面の座標)
    public void Point(Vec2 uv, float screenW)
    {
        u = uv.X;
        v = uv.Y;
        scale = screenW > 0 ? W / screenW : 1;
    }

    public float X => u * W;
    public float Y => v * H;

    // 位置を直した event。マウスの移動の量は、絵の上の画素の数にする
    public EventData Event(EventData e)
    {
        var motion = e.Kind == EventKind.MouseMotion;
        return new EventData
        {
            Kind = e.Kind,
            Key = e.Key,
            Mods = e.Mods,
            Typed = e.Typed,
            Plain = e.Plain,
            Button = e.Button,
            X = X,
            Y = Y,
            Dx = motion ? e.Dx * scale : e.Dx,
            Dy = motion ? e.Dy * scale : e.Dy,
        };
    }
}
