// TestAlloc.cs が App.Use で開く app。毎 frame、lider の検索と等幅の文字の描画を決まった行で呼ぶ。
// 宣言を読む Search.Refresh は file を読むたびに確保するので、読み終えたら呼ばない。
// SpriteBatch は Begin の後の最初の四角で確保するので、MonoText.Draw の前に文字を 1 つ描いておく。
// 行番号を TestAlloc.cs が見るので、行を足したら合わせる。
using System;
using Lub;
using Lubx;
using LiderCore;

public static class TestAllocProbe
{
    static Workspace? work;
    static Search search = new Search();
    static SpriteBatch? sb;
    static MonoText? mono;
    static int[]? keep;
    // 検索が宣言を読み終えたか (TestAlloc.cs が読む)
    static bool Scanned;

    public static void OnFrame(float dt)
    {
        if (work == null)
        {
            work = new Workspace(Environment.GetEnvironmentVariable("LIDER_ROOT") ?? ".");
            search.Open(work);
        }
        if (!Scanned)
            search.Refresh(work);
        sb ??= new SpriteBatch("test_alloc_sb", 320, 180);
        mono ??= new MonoText("test_alloc_mono", (Environment.GetEnvironmentVariable("LIDER_HOME") ?? ".") + "/data/yutapon_coding_081d.ttf", 15, 2);
        keep = new int[8];
        search.Find("NoSuchDeclaration");
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex });
        sb.Begin();
        mono.Glyphs.DrawGlyph(sb, 'x', 0, 0);
        mono.Draw(sb, "abcdef", 0, 20, Color.Rgb(1, 1, 1));
        mono.Draw(sb, "fedcba", 0, 40, Color.Rgb(1, 1, 1));
        sb.Flush();
        Gfx.EndPass();
        Scanned = search.Find("TestAllocProbe") != null;
    }
}
