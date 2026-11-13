// 窓をつかんで動かし、両手で広げる (Grab)、高さと大きさを変えた窓の当たり、キーボードでの移動。
//   - 片手でつかんで指す点を動かすと、つかんだ点と窓の中心の差を保って付いてくる
//   - 両手の間を 2 倍にすると窓が 2 倍になり、Grab.MaxScale と MinScale で止まる
//   - 両手から片手に戻すと、その点からつかみ直して動く
//   - 高さと大きさを変えた窓は、その位置と大きさで ray が当たる
//   - Alt + PageUp で大きくなる
//   - 非 XR では円筒を平面に開き、窓は大きく開く。右ドラッグで窓が動き、ドラッグ中のホイールで大きさが変わり、離すと列の位置に戻る。窓の外の右ドラッグは窓をつかまない
using System;
using Lub;
using LiderCore;

public static class TestGrab
{
    static int frame;
    static bool failed;

    public static void OnInit()
    {
        TestInit();
        Lider.OnInit();
    }

    public static void OnEvent(EventData e)
    {
        Lider.OnEvent(e);
    }

    public static void OnQuit()
    {
        Lider.OnQuit();
    }

    public static void OnFrame(float dt)
    {
        TestFrame(dt);
        Lider.OnFrame(dt);
    }

    static void TestInit()
    {
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    static void Near(float got, float want, string what)
    {
        if (Math.Abs(got - want) > 0.001f)
            Check(false, what + ": " + got + ", want " + want);
    }

    static void TestFrame(float dt)
    {
        frame++;
        if (frame == 1)
            Curved();
        else if (frame == 2)
            Sized();
        else if (frame == 4)
            Dragged();
    }

    static void Curved()
    {
        var target = Environment.GetEnvironmentVariable("LIDER_TEST_FILE");
        var w = Lider.Work;
        Check(target != null && w != null, "LIDER_TEST_FILE or the workspace is missing");
        if (failed)
            return;
        // 画面の真ん中に窓を置くため、インスペクタはしまう
        Lider.Inspector.Shown = false;
        w!.FocusOn(target!);
        var window = w.FocusWindow!;
        // つかむ位置と動かす先は、コードのスクリーンの面の上の (x, y)
        window.Bounds = new Bounds(0.2f, 0.1f, window.W, window.H);

        var grab = new Grab();
        grab.Start(window, 0.25f, 0.0f);
        grab.Move(0.45f, -0.2f);
        Near(window.Bounds!.X, 0.4f, "x after a one-hand move");
        Near(window.Bounds.Y, -0.1f, "height after a one-hand move");

        grab.StartTwo(new Vec3(-0.1f, 0, 0.3f), new Vec3(0.1f, 0, 0.3f));
        grab.MoveTwo(new Vec3(-0.2f, 0, 0.3f), new Vec3(0.2f, 0, 0.3f));
        Near(window.Scale, 2, "scale after doubling the hands' distance");
        grab.Move(0.9f, 0.9f);
        Near(window.Bounds!.X, 0.4f, "a one-hand move during a two-hand grab");
        grab.MoveTwo(new Vec3(-5, 0, 0.3f), new Vec3(5, 0, 0.3f));
        Near(window.Scale, Grab.MaxScale, "scale clamped at the maximum");
        grab.MoveTwo(new Vec3(-0.001f, 0, 0.3f), new Vec3(0.001f, 0, 0.3f));
        Near(window.Scale, Grab.MinScale, "scale clamped at the minimum");

        grab.ReleaseTwo(0.5f, 0.0f);
        grab.Move(0.6f, 0.1f);
        Near(window.Bounds!.X, 0.5f, "x after going back to one hand");
        Near(window.Bounds.Y, 0.0f, "height after going back to one hand");
        grab.Release();
        Check(!grab.Holding, "still holding after release");

        // 高さ 0.2、大きさ 1.5 の窓の上端の近くは、大きさ 1 なら外れる高さでも当たる
        window.Scale = 1.5f;
        var edge = 0.2f + PanelView.BaseH * 1.5f * 0.45f;
        var ray = new Ray(new Vec3(0, 0, 0), new Vec3((float)Math.Sin(0.5f) * Space.R, edge, (float)Math.Cos(0.5f) * Space.R));
        Check(Space.HitCurved(ray, 0.5f, 0.2f, Space.R, PanelView.BaseW * window.Scale, PanelView.BaseH * window.Scale) != null,
            "the scaled and raised window was not hit near its top");
        Check(Space.HitCurved(ray, 0.5f, 0.2f, Space.R, PanelView.BaseW, PanelView.BaseH) == null,
            "an unscaled window was hit where only the scaled one should be");

        window.Bounds = null;
        window.Scale = 1;
        Lider.WindowKey(w, Keys.PageUp);
        Near(window.Scale, 1.1f, "scale after Alt+PageUp");
        window.Bounds = null;
    }

    // lider の frame を 1 度通ると、非 XR の平面で窓が読める大きさになる
    static void Sized()
    {
        var window = Lider.Work!.FocusWindow!;
        Check(Space.Flat, "not flat without XR");
        Near(window.Scale, Space.FlatScale, "scale of a window opened without XR");
        window.Scale = 1;

        // 角度 a、高さ y の所は、平面の x = R * a、y
        var a = 0.25f;
        var ray = new Ray(new Vec3(0, 0, 0), new Vec3(a * Space.R, 0.2f, Space.R));
        var at = Space.OnCylinderAt(ray, Space.R);
        Near(at!.Angle, a, "angle on the plane");
        Near(at.Y, 0.2f, "height on the plane");
        var uv = Space.HitCurved(ray, a, 0.2f, Space.R, PanelView.BaseW, PanelView.BaseH);
        Near(uv!.Value.X, 0.5f, "u at the center of a flat window");
        Near(uv.Value.Y, 0.5f, "v at the center of a flat window");
    }

    static void Dragged()
    {
        var window = Lider.Work!.FocusWindow!;
        Gfx.Size(out var sw, out var sh);
        float cx = sw * 0.5f;
        float cy = sh * 0.5f;

        Mouse(EventKind.MouseButtonDown, 1, 1);
        Mouse(EventKind.MouseMotion, cx, cy);
        Near(Lider.WindowAngle(Lider.Work!, window), 0, "right drag from outside the window moved it");
        Mouse(EventKind.MouseButtonUp, cx, cy);

        Mouse(EventKind.MouseButtonDown, cx, cy);
        Mouse(EventKind.MouseMotion, cx + sw * 0.1f, cy - sh * 0.1f);
        Check(Lider.WindowAngle(Lider.Work!, window) > 0.05f, "right drag to the right: angle " + Lider.WindowAngle(Lider.Work!, window));
        Check(Lider.WindowY(Lider.Work!, window) > 0.05f, "right drag up: y " + Lider.WindowY(Lider.Work!, window));
        Lider.OnEvent(new EventData { Kind = EventKind.MouseWheel, Dy = 1 });
        Near(window.Scale, 1.1f, "scale after the wheel during a right drag");
        Mouse(EventKind.MouseButtonUp, cx + sw * 0.1f, cy - sh * 0.1f);

        Lider.Work!.Arrange(null);
        Near(Lider.WindowAngle(Lider.Work!, window), 0, "the only window did not go back to its place after the release");
        Near(Lider.WindowY(Lider.Work!, window), 0, "the window did not go back to the row after the release");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static void Mouse(EventKind kind, float x, float y)
    {
        Lider.OnEvent(new EventData { Kind = kind, Button = 3, X = x, Y = y });
    }

    static void Check(bool ok, string message)
    {
        if (ok || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}
