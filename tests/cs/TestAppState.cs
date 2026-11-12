// App.Save / App.Load / App.Drop: 開いた app の状態を控えに書き、同じ app か別の app に読み込む。
// TestAppStateChild は、乱数の列と積んだ List から決まる色で main を塗る。
// 開いた側は 2 つの app (a と b) を開き、描かせた texture を読み戻して比べる。
//   frame 4: a の状態を b に読み込む。その後は a と b が同じ色を描く。
//   frame 8: a の状態を控える。frame 12 で a と b に読み込むと、frame 8 からの色をもう一度描く。
using System;
using System.Collections.Generic;
using Lub;

public static class TestAppStateChild
{
    static int frame;
    static int seed = 12345;
    static List<int> seen = new List<int>();

    public static void OnFrame(float dt)
    {
        seed = (seed * 1103515245 + 12345) & 0x7fffffff;
        seen.Add((seed >> 8) % 251);
        var sum = 0;
        foreach (var v in seen) sum = (sum * 7 + v) % 256;
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { sum / 255.0f, (frame % 256) / 255.0f, 0, 1 } });
        Gfx.EndPass();
        frame++;
    }
}

public static class TestAppState
{
    const int loadAt = 4, saveAt = 8, rewindAt = 12, frames = 18;
    static Readback? rbA;
    static Readback? rbB;
    static int frame;
    // 読み戻した色 (frame ごとの赤と緑。-1 はまだ)
    static int[] redA = new int[frames];
    static int[] greenA = new int[frames];
    static int[] redB = new int[frames];
    static int[] greenB = new int[frames];

    public static void OnInit()
    {
        App.Config(new ConfigOpts
        {
            Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu",
            ReadbackFixedLatency = true,
        });
        rbA = Gfx.Readback("app_state_a");
        rbB = Gfx.Readback("app_state_b");
        for (var i = 0; i < frames; i++)
        {
            redA[i] = -1;
            redB[i] = -1;
        }
    }

    static TextureRef? Target(string key)
    {
        var rt = Gfx.UseTexture(key, 4, 4, Gfx.PixelFormat.Rgba8, null, 1,
            new TextureOpts { Target = true, Filter = Gfx.Filter.Nearest });
        Check.That(rt != null, "UseTexture failed");
        return rt;
    }

    static void Read(Readback rb, TextureRef rt, int[] red, int[] green)
    {
        Gfx.ReadTexture(rb, rt, frame, out var st, out var bytes, out _, out _, out _, out _, out var id, out _, out var err);
        if (frame < 2) return;
        Check.That(st == Gfx.ReadbackStatus.Ready, $"frame {frame}: got {st} {err}, want Ready");
        if (st != Gfx.ReadbackStatus.Ready) return;
        red[id] = bytes!.Get(0);
        green[id] = bytes.Get(1);
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        var rtA = Target("a");
        var rtB = Target("b");
        if (rtA == null || rtB == null) return;
        const string module = "tests/cs/.lub/all.tcsb";
        var a = App.Use("a", module, "TestAppStateChild");
        var b = App.Use("b", module, "TestAppStateChild");
        if (frame == 0)
        {
            Check.That(!App.Load(a, 0, out var empty), "Load from an empty slot must fail");
            Check.That(empty != null && empty.Contains("slot 0 is empty"), $"empty slot: got {empty}");
        }
        if (frame == loadAt)
        {
            // b はまだ Step していない。読み込みが module を開く
            Check.That(App.Save(a, 0, out var e1), $"Save: {e1}");
            Check.That(App.Load(b, 0, out var e2), $"Load into another app: {e2}");
        }
        if (frame == saveAt)
            Check.That(App.Save(a, 1, out var e3), $"Save: {e3}");
        if (frame == rewindAt)
        {
            Check.That(App.Load(a, 1, out var e4), $"Load into the same app: {e4}");
            Check.That(App.Load(b, 1, out var e5), $"Load: {e5}");
            App.Drop(1);
            Check.That(!App.Load(a, 1, out var dropped), "Load from a dropped slot must fail");
            Check.That(dropped != null && dropped.Contains("slot 1 is empty"), $"dropped slot: got {dropped}");
        }
        Check.That(App.Step(a, rtA, dt), $"frame {frame}: Step a failed");
        if (frame >= loadAt)
            Check.That(App.Step(b, rtB, dt), $"frame {frame}: Step b failed");
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        Read(rbA!, rtA, redA, greenA);
        Read(rbB!, rtB, redB, greenB);
        frame++;
        if (frame < frames) return;
        // 最後の 2 frame は読み戻しがまだ届いていない
        var last = frames - 2;
        for (var k = 0; k < last; k++)
            Check.That(redA[k] >= 0, $"frame {k}: no readback of a");
        // 止めずに進めた frame の緑は frame の番号
        for (var k = 0; k < rewindAt; k++)
            Check.That(Math.Abs(greenA[k] - k) <= 1, $"frame {k}: a drew frame {greenA[k]}");
        // b は a の状態から続く
        for (var k = loadAt; k < last; k++)
        {
            Check.That(redB[k] == redA[k] && greenB[k] == greenA[k],
                $"frame {k}: b drew {redB[k]},{greenB[k]}, a drew {redA[k]},{greenA[k]}");
        }
        // 読み込んだ後は、控えた frame からの色をもう一度描く
        for (var k = rewindAt; k < last; k++)
        {
            var from = saveAt + (k - rewindAt);
            Check.That(redA[k] == redA[from] && greenA[k] == greenA[from],
                $"frame {k}: a drew {redA[k]},{greenA[k]}, frame {from} drew {redA[from]},{greenA[from]}");
        }
        // 色が frame ごとに違うこと (同じ色ばかりなら上の比較は何も見ていない)
        var distinct = new HashSet<int>();
        for (var k = 0; k < rewindAt; k++) distinct.Add(redA[k]);
        Check.That(distinct.Count >= 6, $"only {distinct.Count} distinct colors");
        Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
