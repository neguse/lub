// Pure function test of the font core API (Font.Metrics / Font.Glyph / Font.GlyphMesh / Font.Kern).
// Both rasterization and triangulation are deterministic on the CPU, so it checks not only properties of the values but also that calls agree with each other.
using System;
using System.Collections.Generic;
using Lub;

public static class TestFont
{
    const string fontPath = "samples/data/fonts/MPLUS1p-subset.ttf";
    // A file that is not a font (input of corrupted data)
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

        // Bytes is a view valid for the frame, so reload it every frame and check in the frame where everything is available
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

        // bitmap glyph: "い" 32px
        var gi = Font.Glyph(ttf, Cp("い"), 32);
        Check.That(gi != null, "glyph い missing");
        if (gi == null)
            return;
        Check.That(gi.W > 0 && gi.H > 0, "glyph い empty bitmap");
        Check.That(gi.Bytes != null && gi.Bytes.Length == gi.W * gi.H, "glyph い bytes size mismatch");
        Check.That(gi.Advance > 0, "glyph い advance");
        if (gi.Bytes == null)
            return;
        // Copy into values for comparison
        var pixels = new List<int>();
        for (int i = 0; i < gi.Bytes.Length; i++)
            pixels.Add(gi.Bytes.Get(i));
        // Determinism: the same input gives the same shape
        var gi2 = Font.Glyph(ttf, Cp("い"), 32);
        Check.That(gi2 != null, "glyph い missing on the second call");
        if (gi2 == null || gi2.Bytes == null)
            return;
        Check.That(gi2.W == gi.W && gi2.H == gi.H && gi2.Xoff == gi.Xoff && gi2.Yoff == gi.Yoff, "glyph い not deterministic");
        var same = gi2.Bytes.Length == pixels.Count;
        for (int i = 0; same && i < pixels.Count; i++)
            same = gi2.Bytes.Get(i) == pixels[i];
        Check.That(same, "glyph い pixels not deterministic");

        // Space glyph: the advance is returned even without a bitmap
        var sp = Font.Glyph(ttf, 0x20, 32);
        Check.That(sp != null && sp.Bytes == null && sp.Advance > 0, "space glyph contract");

        // Outside coverage (Arabic script) is null → fallback is the caller's responsibility
        Check.That(Font.Glyph(ttf, 0x0626, 32) == null, "uncovered codepoint must be nil");

        // mesh glyph: "ほ" (has holes and multiple contours)
        var gm = Font.GlyphMesh(ttf, Cp("ほ"));
        Check.That(gm != null, "mesh ほ missing");
        if (gm == null)
            return;
        Check.That(gm.Mesh.VertCount > 0 && gm.Mesh.IndexCount > 0, "mesh ほ empty");
        Check.That(gm.Mesh.IndexCount % 3 == 0, "mesh ほ index count not triangles");
        for (int i = 0; i < gm.Mesh.IndexCount; i++)
        {
            var idx = gm.Mesh.Indices[i];
            Check.That(idx >= 0 && idx < gm.Mesh.VertCount, "mesh ほ index out of range: " + idx);
        }
        for (int i = 0; i < gm.Mesh.VertCount; i++)
        {
            var x = gm.Mesh.Positions[i * 3];
            var y = gm.Mesh.Positions[i * 3 + 1];
            var z = gm.Mesh.Positions[i * 3 + 2];
            Check.That(z == 0, "mesh ほ z must be 0");
            Check.That(x > -1 && x < 2 && y > -1 && y < 2, "mesh ほ position out of em range");
        }
        Check.That(gm.Advance > 0, "mesh ほ advance");

        // Making the tolerance finer does not reduce the vertices
        var fine = Font.GlyphMesh(ttf, Cp("ほ"), 0.0005f);
        Check.That(fine != null && fine.Mesh.VertCount >= gm.Mesh.VertCount, "finer tolerance must not reduce verts");

        // The mesh of a space glyph is empty but it has an advance
        var spm = Font.GlyphMesh(ttf, 0x20);
        Check.That(spm != null && spm.Mesh.VertCount == 0 && spm.Mesh.IndexCount == 0 && spm.Advance > 0, "space mesh contract");

        // kern is a number (the M+ kana need not have any significant pairs)
        Font.Kern(ttf, Cp("A"), Cp("V"));

        // Integration: upload the glyph bitmap to an R8 texture and feed the mesh into a vertex buffer (the same shape as the real atlas path)
        var nonzero = 0;
        for (int i = 0; i < pixels.Count; i++)
        {
            if (pixels[i] > 0)
                nonzero++;
        }
        Check.That(nonzero > 0, "glyph い bitmap has no coverage");
        var tex = Gfx.UseTexture("font_glyph_i", gi.W, gi.H, Gfx.PixelFormat.R8, pixels, 1);
        Check.That(tex != null, "use_texture with glyph pixels failed");
        var buf = Gfx.UseBuffer("font_mesh_ho", Gfx.BufferType.Storage, gm.Mesh.Positions, 1);
        Check.That(buf != null, "use_buffer with glyph mesh failed");

        // Corrupted data is an error (distinguished from the null of a missing glyph)
        Check.Error("bogus font", "font");
        Font.Metrics(bogus);
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
