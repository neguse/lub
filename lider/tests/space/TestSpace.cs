// 円筒に沿って曲げた板を ray で指した位置 (Space.HitCurved)。
//   - 板の中心の角度へ原点から向けた ray は、板の中心 (0.5, 0.5) に当たる
//   - 角度だけずらすと、u は角度の幅に比例して動く (平らな板なら tan でずれる所)
//   - 高さは、当たった点の高さで決まる
//   - 板の外を向けば当たらない
//   - 原点から少し離れた所 (眼の位置) から出た ray でも、円筒の上の同じ所に当たる
using System;
using Lub;
using LiderCore;

public static class TestSpace
{
    static bool ran;
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

    static Ray Toward(Vec3 origin, float angle, float y, float r)
    {
        var target = new Vec3(r * (float)Math.Sin(angle), y, r * (float)Math.Cos(angle));
        return new Ray(origin, target - origin);
    }

    static void Near(Vec2? uv, float u, float v, string what)
    {
        if (uv == null)
        {
            Fail(what + ": no hit");
            return;
        }
        var hit = uv.Value;
        if (Math.Abs(hit.X - u) > 0.002f || Math.Abs(hit.Y - v) > 0.002f)
            Fail(what + ": hit at " + hit.X + "," + hit.Y + ", want " + u + "," + v);
    }

    static void TestFrame(float dt)
    {
        if (ran)
            return;
        ran = true;
        var r = Space.R;
        var a = 0.4f;
        var w = 1.6f;
        var h = 0.8f;
        var span = w / r;
        var o = new Vec3(0, 0, 0);
        Near(Space.HitCurved(Toward(o, a, 0, r), a, 0, r, w, h), 0.5f, 0.5f, "center");
        Near(Space.HitCurved(Toward(o, a + span * 0.4f, 0, r), a, 0, r, w, h), 0.9f, 0.5f, "toward the right edge");
        Near(Space.HitCurved(Toward(o, a - span * 0.25f, h * 0.25f, r), a, 0, r, w, h), 0.25f, 0.25f, "up and left");
        if (Space.HitCurved(Toward(o, a + span * 0.6f, 0, r), a, 0, r, w, h) != null)
            Fail("a ray outside the board hit it");
        var eye = new Vec3(0.032f, 0.05f, -0.02f);
        Near(Space.HitCurved(Toward(eye, a, 0, r), a, 0, r, w, h), 0.5f, 0.5f, "from the eye");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static void Fail(string message)
    {
        if (failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}
