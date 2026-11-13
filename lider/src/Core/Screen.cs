// スクリーン: 空間の中に置く面。
// 上にウィンドウ (Window) を並べ、どこに並べるかは配置の規則 (ILayout) が決める。
// 形は平面か、視点を通る縦の軸の回りの円筒の一部。固定先は視点か世界。
// 位置は固定先の座標 (+X 右、+Y 上、+Z 前、単位はメートル) で、スクリーンの真ん中の点を持つ。
// 円筒の半径と真ん中の角度は、その点の、軸からの距離と向きで決まる。
// スクリーンの面の上の場所は、スクリーンの真ん中を原点に、右が +X (円筒では弧に沿った長さ)、上が +Y の、メートルで表す。
using System;
using System.Collections.Generic;
using Lub;

namespace LiderCore;

public enum ScreenShape
{
    Plane,
    Cylinder,
}

public enum ScreenAnchor
{
    // 視点に固定する (視点が動いても、画面の同じ所に見える)
    View,
    // 世界に固定する
    World,
}

public class Screen
{
    public readonly string Name;
    public ScreenShape Shape = ScreenShape.Plane;
    public ScreenAnchor Anchor = ScreenAnchor.View;
    // 真ん中の位置 (固定先の座標)
    public Vec3 Position = new Vec3(0, 0, 1);
    // 幅と高さ (メートル)。並べた物で決まるスクリーン (Strip) では使わない
    public Vec2 Size = new Vec2(1, 1);
    // 透過度 (1 で透けない)
    public float Opacity = 1;
    public ILayout Layout = new Full();
    // ウィンドウ (並べる順)
    public readonly List<Window> Windows = new List<Window>();
    // 一つだけを出す規則 (Full) で出すウィンドウ (null なら最初の物)
    public Window? Focus;
    // つかんで動かしているウィンドウ (配置の規則はこれを動かさず、これの場所で並びを入れ替える)
    public Window? Held;

    public Screen(string name)
    {
        Name = name;
    }

    // 視点からの距離。奥のスクリーンから描く
    public float Distance => (float)Math.Sqrt(Position.X * Position.X + Position.Y * Position.Y + Position.Z * Position.Z);

    // 円筒の半径 (縦の軸からの距離)
    public float Radius => (float)Math.Sqrt(Position.X * Position.X + Position.Z * Position.Z);

    // 円筒の真ん中の角度 (+Z が 0、右が正)
    public float Angle => (float)Math.Atan2(Position.X, Position.Z);

    // 面の上の x の所の、円筒の角度
    public float AngleAt(float x)
    {
        return Angle + x / Radius;
    }

    // 円筒の角度 angle の所の、面の上の x
    public float XAt(float angle)
    {
        return (angle - Angle) * Radius;
    }

    // ウィンドウを並べる (配置の規則で)
    public void Arrange()
    {
        Layout.Arrange(this);
    }

    // 今の距離のまま、縦の画角 fov (度) と縦横比 aspect の視野をちょうど覆う大きさにし、正面に置く
    public void Fill(float fov, float aspect)
    {
        Position = new Vec3(0, 0, Position.Z);
        var height = 2 * Position.Z * (float)Math.Tan(fov * 0.5f * Math.PI / 180);
        Size = new Vec2(height * aspect, height);
    }

    // 視点に固定した平面のスクリーンで、ウィンドウを並べ、画面に投影した矩形 (ViewportRect) を決める。
    // viewportW と viewportH は画面の大きさ (画面の座標)、fov は縦の画角 (度)。
    // 視点の正面を向く平面なので、矩形は軸に揃い、画素に揃える
    public void Project(float viewportW, float viewportH, float fov)
    {
        Arrange();
        // 距離 Position.Z の所の 1 メートルが、画面の上で覆う長さ
        var perM = viewportH / (2 * Position.Z * (float)Math.Tan(fov * 0.5f * Math.PI / 180));
        foreach (var window in Windows)
        {
            if (window.Position == null)
            {
                window.ViewportRect = null;
                continue;
            }
            var at = window.Position.Value;
            var size = window.Size;
            var x0 = (float)Math.Round(viewportW * 0.5f + (Position.X + at.X - size.X * 0.5f) * perM);
            var y0 = (float)Math.Round(viewportH * 0.5f - (Position.Y + at.Y + size.Y * 0.5f) * perM);
            var x1 = (float)Math.Round(viewportW * 0.5f + (Position.X + at.X + size.X * 0.5f) * perM);
            var y1 = (float)Math.Round(viewportH * 0.5f - (Position.Y + at.Y - size.Y * 0.5f) * perM);
            window.ViewportRect = new PixelRect(x0, y0, x1 - x0, y1 - y0);
        }
    }
}

// 画面の上の矩形 (左上と大きさ。画面の座標、下が +Y)
public class PixelRect
{
    public readonly float X;
    public readonly float Y;
    public readonly float W;
    public readonly float H;

    public PixelRect(float x, float y, float w, float h)
    {
        X = x;
        Y = y;
        W = w;
        H = h;
    }

    public bool Contains(float x, float y)
    {
        return x >= X && x < X + W && y >= Y && y < Y + H;
    }
}

// スクリーンの上に並ぶ矩形。中身 (IContent) を 1 つ映す
public class Window
{
    public IContent Content;
    // スクリーンの座標での真ん中の位置 (メートル)。X が右、Y が上、Z が奥 (0 が面の上、負が手前)。
    // X と Y は配置の規則か、つかんで動かす操作が決める。まだ決めていないか、出さないウィンドウは null
    public Vec3? Position;
    // 幅と高さ (メートル)。Position と一緒に決める
    public Vec2 Size = new Vec2(0, 0);
    // 画面に投影した場所 (視点に固定した平面のスクリーンだけ。Screen.Project が決める)
    public PixelRect? ViewportRect;
    // 中身を描く画素の数。利用者が決めた数 (WidthPx、HeightPx) と、今の数 (PxW、PxH。Zen の間だけ違う)
    public int WidthPx = PanelView.BasePxW;
    public int HeightPx = PanelView.BasePxH;
    public int PxW = PanelView.BasePxW;
    public int PxH = PanelView.BasePxH;
    // 大きさの倍率 (1 で、BasePxW の画素が PanelView.BaseW メートル)
    public float Scale = 1;
    // 非 XR で読める大きさにしたか
    public bool Sized;

    public const int MinPxW = 320;
    public const int MinPxH = 160;
    public const int MaxPx = 2048;

    public Window(IContent content)
    {
        Content = content;
    }

    // 倍率 1 の幅と高さ (メートル。中身を描く画素の数で決まる)
    public Vec2 UnscaledSize => new Vec2(PanelView.BaseW * PxW / PanelView.BasePxW, PanelView.BaseW * PxH / PanelView.BasePxW);

    // 奥行き (Position の Z。決めていなければ 0)
    public float Z => Position != null ? Position.Value.Z : 0;

    // 真ん中を、スクリーンの面の上の (x, y) に置く。奥行きと大きさは今のまま
    public void Place(float x, float y)
    {
        Position = new Vec3(x, y, Z);
    }

    // 奥行きを変える (まだ置いていなければ何もしない)
    public void SetZ(float z)
    {
        if (Position != null)
            Position = new Vec3(Position.Value.X, Position.Value.Y, z);
    }

    // 中身がコードのときの、その中身
    public Code Code => (Code)Content;

    // 利用者の大きさを変える (px)。文字の桁に揃えなくても エディタが桁を決める
    public void Resize(int pxW, int pxH)
    {
        WidthPx = Math.Max(MinPxW, Math.Min(MaxPx, pxW));
        HeightPx = Math.Max(MinPxH, Math.Min(MaxPx, pxH));
    }
}
