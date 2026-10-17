// 同じフレームで MainTex を clear → render target の pass → ReadTexture(rt)
// → MainTex に Load の pass、の後の capture。読み戻しの後も
// MainTex は同じ image のままで、capture はこのフレームの clear の色になる。
// clear の色はフレームごとに変え、前のフレームの image が写ると色がずれる。
// LUB_READBACK_CAPTURE_TEST_NO_LOAD=1 では最後の Load の pass を省き、読み戻しの
// 後に MainTex の pass が無いフレームも capture できることを確かめる。
// 読み戻しは遅延を保証するモードで、各フレームに 2 フレーム前の結果が届く。
//
// --capture <path> と同じ path を LUB_READBACK_CAPTURE_TEST_OUT に渡して走らせる。
// capture の後に app が終わるので、OnQuit で capture の PNG を確かめる。
using System;
using Lub;

public static class TestReadbackCapture
{
    static readonly int[] rtColor = new int[] { 0, 0, 255, 255 };
    static Readback? rb;
    static int frame;
    static int[]? want;
    static int rtVerified;

    static float[] ClearColor(int k)
    {
        var r = (k % 5) * 0.25f;
        return new float[] { r, 1.0f - r, 0.0f, 1.0f };
    }

    public static void OnInit()
    {
        Check.That(Environment.GetEnvironmentVariable("LUB_READBACK_CAPTURE_TEST_OUT") != null, "LUB_READBACK_CAPTURE_TEST_OUT is not set");
        App.Config(new ConfigOpts
        {
            Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu",
            ReadbackFixedLatency = true,
        });
        rb = Gfx.Readback("rt");
    }

    public static void OnFrame(float dt)
    {
        frame++;
        if (frame >= 300)
        {
            Check.Fail("capture never happened");
            return;
        }
        var c = ClearColor(frame);
        want = new int[] { (int)Math.Floor(c[0] * 255 + 0.5f), (int)Math.Floor(c[1] * 255 + 0.5f), 0, 255 };

        var rt = Gfx.UseTexture("rt", 4, 4, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest });

        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = c });
        Gfx.EndPass();

        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { 0.0f, 0.0f, 1.0f, 1.0f } });
        Gfx.EndPass();

        Gfx.ReadTexture(rb!, rt!, frame, out var st, out var bytes, out _, out _, out _, out _, out var id, out _, out var err);
        if (st == Gfx.ReadbackStatus.Error)
        {
            Check.Fail("read_texture failed: " + err);
            return;
        }
        if ((st == Gfx.ReadbackStatus.Ready) != (frame > 2))
        {
            Check.Fail("frame " + frame + ": read_texture status " + st);
            return;
        }
        if (st == Gfx.ReadbackStatus.Ready)
        {
            if (id != frame - 2)
            {
                Check.Fail("frame " + frame + ": got readback " + id);
                return;
            }
            for (var i = 0; i < 4; i++)
            {
                var got = bytes!.Get(i);
                if (got != rtColor[i])
                {
                    Check.Fail("rt byte " + (i + 1) + ": got " + got);
                    return;
                }
            }
            rtVerified++;
        }

        if (Environment.GetEnvironmentVariable("LUB_READBACK_CAPTURE_TEST_NO_LOAD") != "1")
        {
            Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
            Gfx.EndPass();
        }
    }

    public static void OnQuit()
    {
        if (want == null)
        {
            Check.Fail("no frame was rendered");
            return;
        }
        if (rtVerified != frame - 2)
        {
            Check.Fail(rtVerified + " read_texture(rt) results arrived in " + frame + " frames");
            return;
        }
        var path = Environment.GetEnvironmentVariable("LUB_READBACK_CAPTURE_TEST_OUT")!;
        Png.Load(path, out var bytes, out var w, out var h, out _, out var stride, out _, out var status, out var perr);
        if (status != Io.Status.Ready || bytes == null)
        {
            Check.Fail("capture " + path + " not loaded: " + status + " " + perr);
            return;
        }
        // 中央と四隅
        var px = new int[] { w / 2, 0, w - 1, 0, w - 1 };
        var py = new int[] { h / 2, 0, 0, h - 1, h - 1 };
        for (var k = 0; k < 5; k++)
        {
            var off = py[k] * stride + px[k] * 4;
            for (var i = 0; i < 4; i++)
            {
                var got = bytes.Get(off + i);
                if (Math.Abs(got - want[i]) > 2)
                {
                    Check.Fail("capture (" + px[k] + ", " + py[k] + ") channel " + (i + 1) + ": got " + got + ", want " + want[i] + " (frame " + frame + ")");
                    return;
                }
            }
        }
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
