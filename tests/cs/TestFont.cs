// font core API (Font.Metrics / Font.Glyph / Font.GlyphMesh / Font.Kern) の純関数テスト。ラスタも三角形化も CPU 決定的なので、値の性質だけでなく呼び出し間の一致も見る。
using System;
using System.Collections.Generic;
using Lub;

public static class TestFont
{
    const string fontPath = "samples/data/fonts/MPLUS1p-subset.ttf";
    // フォントではない file (壊れたデータの入力)
    const string bogusPath = "tests/cs/TestFont.csproj";

    static int step;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 320, Height = 180 });
    }

    static int Cp(string s)
    {
        foreach (var rune in s.EnumerateRunes())
            return rune.Value;
        return 0;
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0.02f, 0.03f, 0.04f, 1.0f } });
        Gfx.EndPass();

        // Bytes は frame 有効の view なので、毎 frame 読み直して揃った frame で確かめる
        Io.LoadBytes(fontPath, out var ttf, out _, out var ttfStatus, out var ttfError);
        Io.LoadBytes(bogusPath, out var bogus, out _, out var bogusStatus, out var bogusError);
        if (step == 0)
        {
            Check.That(ttfStatus != Io.Status.Error, "font file missing: " + fontPath + " " + ttfError);
            Check.That(bogusStatus != Io.Status.Error, "bogus file missing: " + bogusPath + " " + bogusError);
            if (ttf == null || bogus == null)
                return;
            step = 1;
            RunChecks(ttf, bogus);
            return;
        }
        Check.Pass();
    }

    static void RunChecks(Bytes ttf, Bytes bogus)
    {
        Check.That(ttf.Length > 0, "font file empty");

        var m = Font.Metrics(ttf);
        Check.That(m.Ascent > 0, "ascent must be positive: " + m.Ascent);
        Check.That(m.Descent < 0, "descent must be negative: " + m.Descent);

        // bitmap glyph: 「い」 32px
        var gi = Font.Glyph(ttf, Cp("い"), 32);
        Check.That(gi != null, "glyph い missing");
        if (gi == null)
            return;
        Check.That(gi.W > 0 && gi.H > 0, "glyph い empty bitmap");
        Check.That(gi.Bytes != null && gi.Bytes.Length == gi.W * gi.H, "glyph い bytes size mismatch");
        Check.That(gi.Advance > 0, "glyph い advance");
        if (gi.Bytes == null)
            return;
        // 比較用に値へ写しておく
        var pixels = new List<int>();
        for (int i = 0; i < gi.Bytes.Length; i++)
            pixels.Add(gi.Bytes.Get(i));
        // 決定性: 同じ入力は同じ形
        var gi2 = Font.Glyph(ttf, Cp("い"), 32);
        Check.That(gi2 != null, "glyph い missing on the second call");
        if (gi2 == null || gi2.Bytes == null)
            return;
        Check.That(gi2.W == gi.W && gi2.H == gi.H && gi2.Xoff == gi.Xoff && gi2.Yoff == gi.Yoff, "glyph い not deterministic");
        var same = gi2.Bytes.Length == pixels.Count;
        for (int i = 0; same && i < pixels.Count; i++)
            same = gi2.Bytes.Get(i) == pixels[i];
        Check.That(same, "glyph い pixels not deterministic");

        // 空白 glyph: bitmap 無しでも advance は返る
        var sp = Font.Glyph(ttf, 0x20, 32);
        Check.That(sp != null && sp.Bytes == null && sp.Advance > 0, "space glyph contract");

        // カバレッジ外 (アラビア文字) は null → fallback は呼び出し側の責務
        Check.That(Font.Glyph(ttf, 0x0626, 32) == null, "uncovered codepoint must be nil");

        // mesh glyph: 「ほ」 (穴・複数輪郭持ち)
        var gm = Font.GlyphMesh(ttf, Cp("ほ"));
        Check.That(gm != null, "mesh ほ missing");
        if (gm == null)
            return;
        Check.That(gm.VertCount > 0 && gm.IndexCount > 0, "mesh ほ empty");
        Check.That(gm.IndexCount % 3 == 0, "mesh ほ index count not triangles");
        for (int i = 0; i < gm.IndexCount; i++)
        {
            var idx = gm.Indices[i];
            Check.That(idx >= 0 && idx < gm.VertCount, "mesh ほ index out of range: " + idx);
        }
        for (int i = 0; i < gm.VertCount; i++)
        {
            var x = gm.Positions[i * 3];
            var y = gm.Positions[i * 3 + 1];
            var z = gm.Positions[i * 3 + 2];
            Check.That(z == 0, "mesh ほ z must be 0");
            Check.That(x > -1 && x < 2 && y > -1 && y < 2, "mesh ほ position out of em range");
        }
        Check.That(gm.Advance > 0, "mesh ほ advance");

        // tolerance を細かくすると頂点は減らない
        var fine = Font.GlyphMesh(ttf, Cp("ほ"), 0.0005f);
        Check.That(fine != null && fine.VertCount >= gm.VertCount, "finer tolerance must not reduce verts");

        // 空白 glyph の mesh は空だが advance を持つ
        var spm = Font.GlyphMesh(ttf, 0x20);
        Check.That(spm != null && spm.VertCount == 0 && spm.IndexCount == 0 && spm.Advance > 0, "space mesh contract");

        // kern は数値 (M+ の kana に有意なペアは無くてよい)
        Font.Kern(ttf, Cp("A"), Cp("V"));

        // 統合: glyph bitmap を R8 テクスチャに上げ、mesh を頂点バッファに流す (実際の atlas 経路と同じ形)
        var nonzero = 0;
        for (int i = 0; i < pixels.Count; i++)
        {
            if (pixels[i] > 0)
                nonzero++;
        }
        Check.That(nonzero > 0, "glyph い bitmap has no coverage");
        var tex = Gfx.UseTexture("font_glyph_i", gi.W, gi.H, Gfx.PixelFormat.R8, pixels, 1);
        Check.That(tex != null, "use_texture with glyph pixels failed");
        var buf = Gfx.UseBuffer("font_mesh_ho", Gfx.BufferType.Storage, gm.Positions, 1);
        Check.That(buf != null, "use_buffer with glyph mesh failed");

        // 壊れたデータはエラー (missing glyph の null とは区別される)
        Check.Error("bogus font", "font");
        Font.Metrics(bogus);
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
