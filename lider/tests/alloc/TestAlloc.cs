// 毎 frame 呼ぶ lider の描画と検索が確保しないこと (Sys.ProfileLines の行ごとの確保で見る)。
//   - Search.Find は、どの宣言とも合わない名前でも、宣言ごとに文字列を作らない
//   - MonoText.Draw は、密度が 1 でなくても、1 文字ごとに文字列を作らない (初めて出た文字だけ)
// 行ごとの負荷は App.Use で .csproj から開いた app にだけ付くので、この entry は lider を動かさず、lider の検索と描画を呼ぶ app (probe/TestAllocProbe.cs) を開いて数える。
// 開いた app では lider のソースの行も数えるので、lider の中の確保は lider の行に付き、lub の cs-lib の中の確保は呼んだ行に付く。
// 呼んだ行 (FindLine、DrawLine) にも lider のどの行にも確保が無く、配列を作る行 (ControlLine) には確保があることを見る。
// frame は開いた app が動き始めてから数える。
// 行番号は TestAllocProbe.cs の中の位置なので、そこに行を足したら ControlLine、FindLine、DrawLine を合わせる。
using System;
using Lub;

public static class TestAlloc
{
    const int ControlLine = 31;
    const int FindLine = 32;
    const int DrawLine = 37;
    static int frames;
    static bool done;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    public static void OnFrame(float dt)
    {
        if (done)
            return;
        var target = Gfx.UseTexture("test_alloc_target", 320, 180, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true });
        if (target == null)
            return;
        var probe = App.Use("test_alloc_probe", (Environment.GetEnvironmentVariable("LIDER_ROOT") ?? ".") + "/probe/TestAllocProbe.csproj");
        var ok = App.Step(probe, target, dt);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        // build を待つ間は Step が false
        if (!ok)
            return;
        frames++;
        if (frames == 3)
            Sys.ProfileLines(true, probe);
        if (frames < 8)
            return;
        int control = 0, find = 0, draw = 0, inside = 0;
        foreach (var p in Sys.TakeProfile(probe))
        {
            // lider のソースの行
            if (!p.Site.Contains("TestAllocProbe.cs:"))
                inside += p.Allocs;
            if (p.Site.EndsWith("TestAllocProbe.cs:" + ControlLine))
                control = p.Allocs;
            if (p.Site.EndsWith("TestAllocProbe.cs:" + FindLine))
                find = p.Allocs;
            if (p.Site.EndsWith("TestAllocProbe.cs:" + DrawLine))
                draw = p.Allocs;
        }
        Sys.ProfileLines(false, probe);
        var scanned = false;
        foreach (var n in Sys.Inspect("TestAllocProbe", 0, 100, probe))
            if (n.Name == "Scanned" && n.Text == "true")
                scanned = true;
        var got = "control " + control + ", Search.Find " + find + ", MonoText.Draw " + draw + ", inside lider " + inside + (scanned ? "" : " (no declarations scanned)");
        done = true;
        if (control == 0 || find != 0 || draw != 0 || inside != 0 || !scanned)
        {
            Console.WriteLine("FAIL allocations over 4 frames: " + got);
            App.Quit(1);
            return;
        }
        Console.WriteLine("PASS " + got);
        App.Quit(0);
    }
}
