// XR 中の --capture が、左右の眼を横に並べた 1 枚の PNG を撮ること。
// 両眼を描いた frame まで capture が待つことも見る: capture の予定の frame を過ぎても何も描かない frame と片眼だけの frame を置く。
// XR セッションの中で走らせる。
// --capture <path> と同じ path を LUB_XR_CAPTURE_TEST_OUT に渡し、--capture-frame は FirstEye より小さくする。
// capture の後に app が終わるので、OnQuit で PNG を確かめる。
using System;
using Lub;

public static class TestXrCapture
{
    const int FirstEye = 8;
    // 眼の target は sRGB なので、0.2 は 124 で PNG に入る
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
