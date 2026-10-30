// Lubx のゲーム向け定型 (Assets.Wav / Assets.RenderTarget / Camera3d.Project / Camera3d.ScreenRay / Ray / Rand.Between / Sfx.Synth / Sfx.FadeGain) の振る舞いを見る。
// 純粋な計算は OnInit で、resource を宣言する定型は frame を回して確かめる。
using System;
using Lub;
using Lubx;

// Sfx.Synth に渡す波形。
// 呼ばれた回数と最初の t、最後の u を覚える
public class TestLubxGameHelpersVoice
{
    public static int Calls;
    public static float FirstT = -1;
    public static float LastU;
    int phase;

    public float Sample(float t, float u)
    {
        Calls++;
        if (FirstT < 0)
            FirstT = t;
        LastU = u;
        phase++;
        return phase * 10;
    }
}

public static class TestLubxGameHelpers
{
    const int sweepFrames = 2;
    // Assets.Wav が読む wav
    const string wavPath = "tests/cs/data/test_audio_decode.wav";

    static int frame;
    static int? wavHandle;
    static int firstSynth;

    static void Near(float a, float b, float eps, string message)
    {
        Check.That(Math.Abs(a - b) <= eps, message + ": " + a + " vs " + b);
    }

    static void CheckFade()
    {
        Near(Sfx.FadeGain(0, 100, 0), 1.0f, 0, "no fade must keep every sample");
        Near(Sfx.FadeGain(99, 100, 0), 1.0f, 0, "no fade must keep the last sample");
        Near(Sfx.FadeGain(89, 100, 10), 1.0f, 0, "samples before the fade must be untouched");
        Near(Sfx.FadeGain(90, 100, 10), 0.9f, 1e-6f, "the fade starts just below 1");
        Near(Sfx.FadeGain(95, 100, 10), 0.4f, 1e-6f, "the fade is linear");
        Near(Sfx.FadeGain(99, 100, 10), 0.0f, 0, "the last sample must reach 0");
    }

    static void CheckRand()
    {
        var a = new Rand(42);
        var b = new Rand(42);
        var seen = new bool[] { false, false, false, false };
        for (int i = 0; i < 2000; i++)
        {
            var v = a.Between(3, 6);
            Check.That(v == 3 + b.NextInt(4), "between must be min + next_int(max - min + 1)");
            Check.That(v >= 3 && v <= 6, "between out of range: " + v);
            if (v >= 3 && v <= 6)
                seen[v - 3] = true;
        }
        Check.That(seen[0] && seen[1] && seen[2] && seen[3], "between must reach both ends");
        Check.That(a.Between(5, 5) == 5, "between(5, 5) must be 5");
        Check.That(a.Between(-2, -1) <= -1, "between must accept negative ranges");
    }

    // ゲームの床のマス選び: 画面の点 -> レイ -> 床 (y = 0) との交点
    static Vec3? PickFloor(Mat4 cam, float sx, float sy, float w, float h)
    {
        var ray = Camera3d.ScreenRay(cam, sx, sy, w, h);
        var t = ray.IntersectPlane(new Vec3(0, 0, 0), new Vec3(0, 1, 0));
        if (t == null)
            return null;
        return ray.At(t.Value);
    }

    static void CheckCamera()
    {
        float w = 1280;
        float h = 720;
        var vp = Camera3d.Vp(new Camera3dOpts { Eye = new Vec3(0, 10, -10), Target = new Vec3(0, 0, 0), Aspect = w / h });
        var c = Camera3d.Project(vp, 0, 0, 0, w, h);
        Check.That(c != null, "target must project");
        if (c == null)
            return;
        Near(c.X, w / 2, 1e-3f, "target x is the screen center");
        Near(c.Y, h / 2, 1e-3f, "target y is the screen center");
        Check.That(c.Z > 0 && c.Z < 1, "depth must be in (0, 1)");

        var right = Camera3d.Project(vp, 3, 0, 0, w, h);
        var up = Camera3d.Project(vp, 0, 0, 3, w, h);
        Check.That(right != null && up != null, "points in front must project");
        if (right == null || up == null)
            return;
        Check.That(right.X > w / 2, "+x is right of center");
        Check.That(up.Y < h / 2, "+z (away from the eye) is above center");

        var behind = Camera3d.Project(vp, 0, 15, -15, w, h);
        Check.That(behind == null, "a point behind the camera must not project");

        var mid = PickFloor(vp, w / 2, h / 2, w, h);
        Check.That(mid != null, "center must hit the ground");
        if (mid == null)
            return;
        Near(mid.X, 0, 1e-4f, "center hit x");
        Near(mid.Y, 0, 1e-4f, "hit is on the ground");
        Near(mid.Z, 0, 1e-4f, "center hit z");

        var picks = new float[] { 3, 2, -5, 7, 1.5f, -4 };
        for (int i = 0; i < picks.Length; i += 2)
        {
            var s = Camera3d.Project(vp, picks[i], 0, picks[i + 1], w, h);
            Check.That(s != null, "round trip must project");
            if (s == null)
                return;
            var hit = PickFloor(vp, s.X, s.Y, w, h);
            Check.That(hit != null, "round trip must hit");
            if (hit == null)
                return;
            Near(hit.X, picks[i], 1e-3f, "round trip x");
            Near(hit.Z, picks[i + 1], 1e-3f, "round trip z");
        }

        var ray = Camera3d.ScreenRay(vp, w / 2, h / 2, w, h);
        Near(ray.Dir.Length(), 1, 1e-6f, "ray dir is normalized");
        Near(ray.Dir.X, 0, 1e-6f, "center ray dir x");
        Near(ray.Dir.Y, -(float)Math.Sqrt(0.5f), 1e-4f, "center ray dir y");
        Near(ray.Dir.Z, (float)Math.Sqrt(0.5f), 1e-4f, "center ray dir z");

        var level = Camera3d.Vp(new Camera3dOpts { Eye = new Vec3(0, 1, 0), Target = new Vec3(0, 1, 10), Aspect = w / h });
        Check.That(PickFloor(level, w / 2, h / 2, w, h) == null, "a ray parallel to the ground has no hit");
        Check.That(PickFloor(level, w / 2, 0, w, h) == null, "a ray above the horizon has no hit");
        var low = PickFloor(level, w / 2, h - 1, w, h);
        Check.That(low != null && low.Z > 0, "a ray below the horizon hits ahead of the eye");
    }

    static void CheckRay()
    {
        var ray = new Ray(new Vec3(1, 5, 2), new Vec3(0, -3, 0));
        Near(ray.Dir.Y, -1, 1e-9f, "Ray normalizes dir");
        var up = new Vec3(0, 1, 0);
        var t = ray.IntersectPlane(new Vec3(0, 2, 0), up);
        Check.That(t != null, "distance to the plane y = 2");
        if (t == null)
            return;
        Near(t.Value, 3, 1e-6f, "distance to the plane y = 2");
        var p = ray.At(3);
        Near(p.X, 1, 1e-6f, "at x");
        Near(p.Y, 2, 1e-6f, "at y");
        Check.That(ray.IntersectPlane(new Vec3(0, 9, 0), up) == null, "a plane behind the origin has no hit");
        Check.That(ray.IntersectPlane(new Vec3(0, 0, 0), new Vec3(1, 0, 0)) == null, "a parallel plane has no hit");
        var slanted = new Ray(new Vec3(0, 1, 0), new Vec3(1, -1, 0));
        var st = slanted.IntersectPlane(new Vec3(0, 0, 0), up);
        Check.That(st != null, "slanted ray distance");
        if (st != null)
            Near(st.Value, (float)Math.Sqrt(2), 1e-6f, "slanted ray distance");
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts
        {
            Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu",
            Width = 320,
            Height = 180,
            ResourceSweepAfterFrames = sweepFrames,
        });
        CheckFade();
        CheckRand();
        CheckCamera();
        CheckRay();
    }

    public static void OnFrame(float dt)
    {
        frame++;

        var rt = Assets.RenderTarget("game_helpers_rt", 64, 32, Gfx.PixelFormat.Rgba8);
        Check.That(rt != null, "render target must be declared");
        if (rt == null)
            return;
        Check.That(rt.Version == 64 * 65536 + 32, "render target version must follow the size");
        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        var nearest = Assets.RenderTarget("game_helpers_rt2", 8, 8, Gfx.PixelFormat.Rgba8, Gfx.Filter.Nearest, Gfx.Wrap.Repeat);
        Check.That(nearest != null, "render target with filter / wrap must be declared");

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();

        // 宣言を止めて wav と synth の snd を sweep させる
        if (frame > 11 && frame < 11 + sweepFrames + 3)
            return;

        var handle = Assets.Wav("game_helpers_wav", wavPath);
        if (wavHandle == null)
        {
            wavHandle = handle;
            if (frame > 10)
                Check.Fail("wav must become ready within 10 frames");
            return;
        }
        if (frame <= 11)
            Check.That(handle == wavHandle, "a ready wav must keep returning the same handle");

        if (frame == 11)
        {
            var voice = new TestLubxGameHelpersVoice();
            firstSynth = Sfx.Synth("game_helpers_synth", 0.01f, 1, (t, u) => voice.Sample(t, u));
            Check.That(firstSynth != 0, "synth must return a snd handle");
            Check.That(TestLubxGameHelpersVoice.Calls == 441, "synth must call sample once per sample (" + TestLubxGameHelpersVoice.Calls + ")");
            Check.That(TestLubxGameHelpersVoice.FirstT == 0, "first sample is at t = 0");
            Check.That(TestLubxGameHelpersVoice.LastU < 1 && TestLubxGameHelpersVoice.LastU > 0.99f, "u must approach 1 at the end");
            var againVoice = new TestLubxGameHelpersVoice();
            var againSynth = Sfx.Synth("game_helpers_synth", 0.01f, 1, (t, u) => againVoice.Sample(t, u));
            Check.That(againSynth == firstSynth, "the same key / version must return the same handle");
            Check.That(TestLubxGameHelpersVoice.Calls == 441, "the same version must not call sample again");
            var newVoice = new TestLubxGameHelpersVoice();
            Sfx.Synth("game_helpers_synth", 0.01f, 2, (t, u) => newVoice.Sample(t, u));
            Check.That(TestLubxGameHelpersVoice.Calls == 882, "a new version must rebuild the waveform");
        }
        else if (frame == 11 + sweepFrames + 3)
        {
            Check.That(handle != null, "a swept wav must be declared again from the file");
            var rebuiltVoice = new TestLubxGameHelpersVoice();
            var rebuilt = Sfx.Synth("game_helpers_synth", 0.01f, 2, (t, u) => rebuiltVoice.Sample(t, u));
            Check.That(rebuilt != 0, "a swept synth must be recreated from the cache");
            Check.That(TestLubxGameHelpersVoice.Calls == 882, "recreating a swept synth must not call sample");
            Check.Pass();
        }
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
