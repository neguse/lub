// 窓をつかんで動かし、両手で広げる。
// 片手は、つかんだ点と窓の真ん中の差 (スクリーンの面の上の位置) を保って、指す点に付いていく。
// 両手は、つかんだときの手の間の距離に対する今の距離の比で、大きさを変える。
using System;
using Lub;

namespace LiderCore;

public class Grab
{
    public const float MinScale = 0.5f;
    public const float MaxScale = 3;
    // つかんだ窓を手前へ出す距離 (メートル)
    public const float LiftDistance = 0.08f;
    public Window? Window;
    float xOffset;
    float yOffset;
    // 両手でつかんでいる間
    bool two;
    float startDistance;
    float startScale;

    public bool Holding => Window != null;

    // スクリーンの面の上の (x, y) で window をつかむ
    public void Start(Window window, float x, float y)
    {
        Window = window;
        two = false;
        xOffset = (window.Position != null ? window.Position.Value.X : x) - x;
        yOffset = (window.Position != null ? window.Position.Value.Y : 0) - y;
    }

    // 指す点がスクリーンの面の上の (x, y) に動いた
    public void Move(float x, float y)
    {
        if (Window == null || two)
            return;
        Place(Window, x + xOffset, y + yOffset);
    }

    // つかんでいる窓の大きさをホイールの dy 段だけ変える
    public void Zoom(float dy)
    {
        if (Window != null)
            Rescale(Window, Window.Scale * (float)Math.Pow(1.1f, dy));
    }

    // 窓の真ん中を (x, y) に置く (大きさは今の倍率で)
    static void Place(Window window, float x, float y)
    {
        window.Place(x, y);
        window.Size = window.UnscaledSize * window.Scale;
    }

    static void Rescale(Window window, float scale)
    {
        window.Scale = Math.Max(MinScale, Math.Min(MaxScale, scale));
        if (window.Position != null)
            Place(window, window.Position.Value.X, window.Position.Value.Y);
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
        Rescale(Window, startScale * (a - b).Length() / startDistance);
    }

    // 片手を離した (両手のときは片手に戻り、指す点からつかみ直す)
    public void ReleaseTwo(float x, float y)
    {
        if (Window == null)
            return;
        Start(Window, x, y);
    }

    public void Release()
    {
        Window = null;
        two = false;
    }
}
