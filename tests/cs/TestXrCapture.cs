// --capture during XR takes a single PNG with the left and right eyes side by side.
// Also checks that capture waits for a frame in which both eyes were drawn: past the scheduled capture frame, it places a frame that draws nothing and a frame with only one eye.
// Run inside an XR session.
// Pass the same path as --capture <path> to LUB_XR_CAPTURE_TEST_OUT, and make --capture-frame smaller than FirstEye.
// The app exits after the capture, so the PNG is checked in OnQuit.
using System;
using Lub;

public static class TestXrCapture
{
    const int FirstEye = 8;
    // The eye targets are sRGB, so 0.2 goes into the PNG as 124
    static readonly float[][] clear = new float[][]
    {
        new float[] { 1.0f, 0.0f, 0.2f, 1.0f },
        new float[] { 0.2f, 0.0f, 1.0f, 1.0f },
    };
    static readonly int[][] want = new int[][]
    {
        new int[] { 255, 0, 124, 255 },
        new int[] { 124, 0, 255, 255 },
    };
    static int frame;
    static int eyeWidth;
    static int eyeHeight;

    public static void OnInit()
    {
        Check.That(Environment.GetEnvironmentVariable("LUB_XR_CAPTURE_TEST_OUT") != null, "LUB_XR_CAPTURE_TEST_OUT is not set");
    }

    public static void OnFrame(float dt)
    {
        frame++;
        if (frame >= 1000)
        {
            Check.Fail("capture never happened");
            return;
        }
        if (!Xr.Active())
        {
            Check.Fail("XR is not active");
            return;
        }
        if (frame < FirstEye)
            return;
        for (var eye = 0; eye < (frame == FirstEye ? 1 : 2); eye++)
        {
            var view = Xr.View(eye, 0.05f, 500);
            if (view == null)
                return;
            eyeWidth = view.Width;
            eyeHeight = view.Height;
            Gfx.BeginPass(new PassOpts { Target = view.Target, ClearColor = clear[eye] });
            Gfx.EndPass();
        }
    }

    public static void OnQuit()
    {
        var path = Environment.GetEnvironmentVariable("LUB_XR_CAPTURE_TEST_OUT")!;
        Png.Load(path, out var bytes, out var w, out var h, out _, out var stride, out _, out var status, out var error);
        if (status != Io.Status.Ready || bytes == null)
        {
            Check.Fail("capture " + path + " not loaded: " + status + " " + error);
            return;
        }
        if (w != eyeWidth * 2 || h != eyeHeight)
        {
            Check.Fail("capture is " + w + "x" + h + ", eye is " + eyeWidth + "x" + eyeHeight);
            return;
        }
        for (var eye = 0; eye < 2; eye++)
        {
            var left = eye * eyeWidth;
            var right = left + eyeWidth - 1;
            var px = new int[] { left, right, left + eyeWidth / 2, left, right };
            var py = new int[] { 0, 0, h / 2, h - 1, h - 1 };
            for (var k = 0; k < 5; k++)
            {
                var off = py[k] * stride + px[k] * 4;
                for (var i = 0; i < 4; i++)
                {
                    var got = bytes.Get(off + i);
                    if (Math.Abs(got - want[eye][i]) > 2)
                    {
                        Check.Fail("eye " + eye + " (" + px[k] + ", " + py[k] + ") channel " + (i + 1) + ": got " + got + ", want " + want[eye][i]);
                        return;
                    }
                }
            }
        }
        Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
