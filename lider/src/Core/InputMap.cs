// 画面の座標の入力の位置を、ゲームの texture の画素に直す。
// 入力を測るウィンドウの矩形 (画面の座標) の中の位置を、texture の同じ割合の位置にする。
// 保存した版と編集中の版を並べて入力を揃えるとき、どちらのゲームも、マウスのあるウィンドウの中の位置を受け取る。
using Lub;

namespace LiderCore;

public class InputMap
{
    float x0;
    float y0;
    float scaleX = 1;
    float scaleY = 1;
    // texture の画素の数
    public int W = 1;
    public int H = 1;

    // from は入力を測るウィンドウの矩形、w と h は texture の画素の数
    public void Set(Bounds from, int w, int h)
    {
        x0 = from.X;
        y0 = from.Y;
        W = w;
        H = h;
        scaleX = from.W > 0 ? w / from.W : 1;
        scaleY = from.H > 0 ? h / from.H : 1;
    }

    public float X(float x)
    {
        return (x - x0) * scaleX;
    }

    public float Y(float y)
    {
        return (y - y0) * scaleY;
    }

    // 位置を直した event (写し方が何も変えなければ e のまま)
    public EventData Event(EventData e)
    {
        if (x0 == 0 && y0 == 0 && scaleX == 1 && scaleY == 1)
            return e;
        return new EventData
        {
            Kind = e.Kind,
            Key = e.Key,
            Mods = e.Mods,
            Typed = e.Typed,
            Plain = e.Plain,
            Button = e.Button,
            X = X(e.X),
            Y = Y(e.Y),
            Dx = e.Dx * scaleX,
            Dy = e.Dy * scaleY,
        };
    }
}
