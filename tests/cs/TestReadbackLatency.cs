// The frame in which readback results arrive.
// Every frame, clears a render target with a color that differs per frame, and reads it back with the frame number as the id.
// TestReadbackFixedLatency: in the latency-guaranteeing mode (ConfigOpts.ReadbackFixedLatency), a request from frame N becomes Ready exactly at the poll in frame N+2, and earlier polls stay Processing even if the GPU has finished.
// TestReadbackNoWait: in the non-guaranteeing mode, the GPU is not waited for readbacks.
// The commands of a frame are not sent to the GPU until the end of that frame, so a poll within the frame that queued the request does not get it, and it arrives in a later frame.
using System;
using Lub;

public static class TestReadbackLatency
{
    // The red of the render target at frame k.
    // Checks which frame the arrived pixels belong to.
    static int Red(int k) { return (k % 8) * 32; }

    public static TextureRef? Draw(int frame)
    {
        var rt = Gfx.UseTexture("rt", 4, 4, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest });
        if (rt == null) return null;
        Gfx.BeginPass(new PassOpts { Target = rt, ClearColor = new float[] { Red(frame) / 255.0f, 0.0f, 1.0f, 1.0f } });
        Gfx.EndPass();
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        return rt;
    }

    public static void ExpectPixels(Bytes bytes, int id)
    {
        var want = new int[] { Red(id), 0, 255, 255 };
        for (var i = 0; i < 4; i++)
        {
            var got = bytes.Get(i);
            Check.That(Math.Abs(got - want[i]) <= 1, $"readback {id} byte {i}: got {got}, want {want[i]}");
        }
    }
}

public static class TestReadbackFixedLatency
{
    const int frames = 12;
    static Readback? rb;
    static int frame;

    public static void OnInit()
    {
        App.Config(new ConfigOpts
        {
            Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu",
            ReadbackFixedLatency = true,
        });
        rb = Gfx.Readback("fixed");
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        var rt = TestReadbackLatency.Draw(frame);
        Check.That(rt != null, "UseTexture failed");
        if (rt == null) return;
        Gfx.ReadTexture(rb!, rt, frame, out var st, out var bytes, out _, out _, out _, out _, out var id, out _, out var err);
        if (frame < 2)
        {
            Check.That(st == Gfx.ReadbackStatus.Processing, $"frame {frame}: got {st} {err}, want Processing");
        }
        else
        {
            Check.That(st == Gfx.ReadbackStatus.Ready, $"frame {frame}: got {st} {err}, want Ready");
            if (st != Gfx.ReadbackStatus.Ready) return;
            Check.That(id == frame - 2, $"frame {frame}: got readback {id}, want {frame - 2}");
            TestReadbackLatency.ExpectPixels(bytes!, id);
        }
        // The next request (frame - 1) is not handed over in this frame even if the GPU has finished
        Gfx.ReadTexture(rb!, rt, null, out st, out _, out _, out _, out _, out _, out id, out _, out err);
        Check.That(st == Gfx.ReadbackStatus.Processing, $"frame {frame}: readback {id} came early ({st} {err})");
        frame++;
        if (frame == frames)
            Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}

public static class TestReadbackNoWait
{
    const int rounds = 5;
    static Readback? rb;
    static int frame;
    static int pending = -1;
    static int done;

    public static void OnInit()
    {
        App.Config(new ConfigOpts
        {
            Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu",
            ReadbackFixedLatency = false,
        });
        rb = Gfx.Readback("no_wait");
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        var rt = TestReadbackLatency.Draw(frame);
        Check.That(rt != null, "UseTexture failed");
        if (rt == null) return;
        if (pending < 0)
        {
            Gfx.ReadTexture(rb!, rt, frame, out var st, out _, out _, out _, out _, out _, out _, out _, out var err);
            Check.That(st == Gfx.ReadbackStatus.Processing, $"frame {frame}: got {st} {err}, want Processing");
            pending = frame;
            Gfx.ReadTexture(rb!, rt, null, out st, out _, out _, out _, out _, out _, out _, out _, out err);
            Check.That(st == Gfx.ReadbackStatus.Processing, $"readback {frame} arrived in the frame that requested it ({st} {err})");
        }
        else
        {
            Gfx.ReadTexture(rb!, rt, null, out var st, out var bytes, out _, out _, out _, out _, out var id, out _, out var err);
            Check.That(st == Gfx.ReadbackStatus.Processing || st == Gfx.ReadbackStatus.Ready, $"frame {frame}: got {st} {err}");
            if (st == Gfx.ReadbackStatus.Ready)
            {
                Check.That(id == pending, $"frame {frame}: got readback {id}, want {pending}");
                TestReadbackLatency.ExpectPixels(bytes!, id);
                pending = -1;
                done++;
            }
            else
            {
                Check.That(frame - pending < 120, $"readback {pending} has not arrived in {frame - pending} frames");
            }
        }
        frame++;
        if (done == rounds)
            Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
