// The app that TestAlloc.cs opens with App.Use. Every frame, it calls lider's search and monospace text drawing on fixed lines.
// Search.Refresh, which reads declarations, allocates every time it reads a file, so it is not called after reading finishes.
// SpriteBatch allocates on the first quad after Begin, so one character is drawn before MonoText.Draw.
// TestAlloc.cs looks at the line numbers, so update it when adding lines.
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
    // Whether search has finished reading declarations (read by TestAlloc.cs)
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
        mono ??= new MonoText("test_alloc_mono", (Environment.GetEnvironmentVariable("LIDER_HOME") ?? ".") + "/data/UDEVGothic-Regular.ttf", 15, 2);
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
