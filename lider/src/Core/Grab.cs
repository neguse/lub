// 窓をつかんで動かし、両手で広げる。
// 片手は、つかんだ点と窓の中心の差 (円筒の角度と高さ) を保って、指す点に付いていく。
// 両手は、つかんだときの手の間の距離に対する今の距離の比で、大きさを変える。
using System;
using Lub;

namespace LiderCore;

public class Grab
{
    public const float MinScale = 0.5f;
    public const float MaxScale = 3;
    public Window? Window;
    float angleOffset;
    float yOffset;
    // 両手でつかんでいる間
    bool two;
    float startDistance;
    float startScale;

    public bool Holding => Window != null;

    // 円筒の上の (angle, y) で window をつかむ
    public void Start(Window window, float angle, float y)
    {
        Window = window;
        two = false;
        angleOffset = (window.Angle ?? angle) - angle;
        yOffset = window.Y - y;
    }

    // 指す点が円筒の上の (angle, y) に動いた
    public void Move(float angle, float y)
    {
        if (Window == null || two)
            return;
        Window.Angle = angle + angleOffset;
        Window.Y = y + yOffset;
    }

    // つかんでいる窓の大きさをホイールの dy 段だけ変える
    public void Zoom(float dy)
    {
        if (Window != null)
            Window.Scale = Math.Max(MinScale, Math.Min(MaxScale, Window.Scale * (float)Math.Pow(1.1f, dy)));
    }

    // もう片方の手でもつかんだ (手の位置は a と b)
    public void StartTwo(Vec3 a, Vec3 b)
    {
        if (Window == null)
            return;
        two = true;
        startDistance = Math.Max(0.01f, (a - b).Length());
        startScale = Window.Scale;
    }

    public void MoveTwo(Vec3 a, Vec3 b)
    {
        if (Window == null || !two)
            return;
        var s = startScale * (a - b).Length() / startDistance;
        Window.Scale = Math.Max(MinScale, Math.Min(MaxScale, s));
    }

    // 片手を離した (両手のときは片手に戻り、指す点からつかみ直す)
    public void ReleaseTwo(float angle, float y)
    {
        if (Window == null)
            return;
        Start(Window, angle, y);
    }

    public void Release()
    {
        Window = null;
        two = false;
    }
}
