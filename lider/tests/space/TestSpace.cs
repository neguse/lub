// Position where a ray points at a board curved along the cylinder (Space.HitCurved).
//   - A ray from the origin toward the angle of the board's center hits the board's center (0.5, 0.5)
//   - Shifting only the angle moves u in proportion to the angle span (where a flat board would shift by tan)
//   - The height is determined by the height of the hit point
//   - Pointing outside the board does not hit
//   - A ray starting slightly away from the origin (at the eye position) also hits the same place on the cylinder
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
