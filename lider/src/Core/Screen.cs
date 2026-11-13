// スクリーン: 空間の中に置く面。
// 上にウィンドウ (Window) を並べ、どこに並べるかは配置の規則 (ILayout) が決める。
// 今は、視点に固定して視点の正面を向く平面だけを扱う。
// 位置は視点の座標 (+X 右、+Y 上、+Z 前、単位はメートル) で持つ。
// 視点の正面を向くので画面には軸に揃った矩形に映り、ウィンドウの画面の上の矩形は画素に揃える。
using System;
using System.Collections.Generic;
using Lub;

namespace LiderCore;

public class Screen
{
    public readonly string Name;
    // 中心の位置 (視点の座標)
    public Vec3 Position = new Vec3(0, 0, 1);
    // 幅と高さ (メートル)
    public float Width = 1;
    public float Height = 1;
    // 透過度 (1 で透けない)
    public float Opacity = 1;
    public ILayout Layout = new Full();
    // ウィンドウ (並べる順)
    public readonly List<Window> Windows = new List<Window>();
    // キーボードの入力が入るウィンドウ (null なら、どれにも入らない)
    public Window? Focus;

    public Screen(string name)
    {
        Name = name;
    }

    // 視点からの距離。奥のスクリーンから描く
    public float Distance => (float)Math.Sqrt(Position.X * Position.X + Position.Y * Position.Y + Position.Z * Position.Z);

    // 今の距離のまま、縦の画角 fov (度) と縦横比 aspect の視野をちょうど覆う大きさにし、正面に置く
    public void Fill(float fov, float aspect)
    {
        Position = new Vec3(0, 0, Position.Z);
        Height = 2 * Position.Z * (float)Math.Tan(fov * 0.5f * Math.PI / 180);
        Width = Height * aspect;
    }

    // 配置の規則でウィンドウを並べ、画面に投影した矩形 (ViewportBounds) を決める。
    // viewportW と viewportH は画面の大きさ (画面の座標)、fov は縦の画角 (度)
    public void Project(float viewportW, float viewportH, float fov)
    {
        Layout.Arrange(this);
        // 距離 Position.Z の所の 1 メートルが、画面の上で覆う長さ
        var perM = viewportH / (2 * Position.Z * (float)Math.Tan(fov * 0.5f * Math.PI / 180));
        var left = viewportW * 0.5f + (Position.X - Width * 0.5f) * perM;
        var top = viewportH * 0.5f - (Position.Y + Height * 0.5f) * perM;
        foreach (var window in Windows)
        {
            var b = window.Bounds;
            if (b == null)
            {
                window.ViewportBounds = null;
                continue;
            }
            var x0 = (float)Math.Round(left + b.X * perM);
            var y0 = (float)Math.Round(top + b.Y * perM);
            var x1 = (float)Math.Round(left + (b.X + b.W) * perM);
            var y1 = (float)Math.Round(top + (b.Y + b.H) * perM);
            window.ViewportBounds = new Bounds(x0, y0, x1 - x0, y1 - y0);
        }
    }
}

// 矩形 (左上と大きさ。下が +Y)
public class Bounds
{
    public readonly float X;
    public readonly float Y;
    public readonly float W;
    public readonly float H;

    public Bounds(float x, float y, float w, float h)
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
    // スクリーンの上の場所 (メートル、スクリーンの左上が 0,0)。配置の規則が決め、出さないウィンドウは null
    public Bounds? Bounds;
    // 画面の上の場所 (画面の座標、画素に揃える)。Screen.Project が決め、出さないウィンドウは null
    public Bounds? ViewportBounds;

    public Window(IContent content)
    {
        Content = content;
    }
}
