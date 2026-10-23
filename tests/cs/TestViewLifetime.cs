// runtime の memory を指す view (Bytes と数値配列) は、返した frame の終わりまで有効。同じ frame の中で同じ API を呼び直しても、前の呼び出しの view は前の中身のまま読める。次の frame に持ち越した view は読むと stale の error になる。
// (Lua 版は stale の bytes に tostring と数値配列に get(0) も試していたが、C# の面に無いので移していない。)
using System;
using System.Collections.Generic;
using Lub;

public static class TestViewLifetime
{
    const string fontPath = "samples/data/fonts/MPLUS1p-subset.ttf";
    // Lua 版の cp("い") / cp("ほ")
    const int cpI = 0x3044;
    const int cpHo = 0x307B;

    static int frame;
    static int step;
    static Bytes? heldBytes;
    static List<float>? heldPositions;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 320, Height = 180 });
    }

    // view の中身を自分の memory に写す (比較用)
    static List<float> Copy(List<float> view)
    {
        var o = new List<float>();
        for (int i = 0; i < view.Count; i++)
            o.Add(view[i]);
        return o;
    }

    static List<int> CopyInts(List<int> view)
    {
        var o = new List<int>();
        for (int i = 0; i < view.Count; i++)
            o.Add(view[i]);
        return o;
    }

    // ASCII の中身を文字列にする (view の中身を比べるため)
    static string Text(Bytes view)
    {
        var o = "";
        for (int i = 0; i < view.Length; i++)
            o += (char)view.Get(i);
        return o;
    }

    static List<int> CopyBytes(Bytes view)
    {
        var o = new List<int>();
        for (int i = 0; i < view.Length; i++)
            o.Add(view.Get(i));
        return o;
    }

    static void Same(List<float> view, List<float> expected, string what)
    {
        Check.That(view.Count == expected.Count, what + ": length changed " + expected.Count + " -> " + view.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            if (view[i] != expected[i])
            {
                Check.Fail(what + ": element " + (i + 1) + " changed " + expected[i] + " -> " + view[i]);
                return;
            }
        }
    }

    static void SameInts(List<int> view, List<int> expected, string what)
    {
        Check.That(view.Count == expected.Count, what + ": length changed " + expected.Count + " -> " + view.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            if (view[i] != expected[i])
            {
                Check.Fail(what + ": element " + (i + 1) + " changed " + expected[i] + " -> " + view[i]);
                return;
            }
        }
    }

    static void SameBytes(Bytes view, List<int> expected, string what)
    {
        Check.That(view.Length == expected.Count, what + ": length changed " + expected.Count + " -> " + view.Length);
        for (int i = 0; i < expected.Count; i++)
        {
            if (view.Get(i) != expected[i])
            {
                Check.Fail(what + ": element " + (i + 1) + " changed " + expected[i] + " -> " + view.Get(i));
                return;
            }
        }
    }

    static List<float> SphereGrid(int n, float r)
    {
        var grid = new List<float>();
        float c = (n - 1) / 2.0f;
        for (int z = 0; z < n; z++)
        {
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float dx = x - c;
                    float dy = y - c;
                    float dz = z - c;
                    grid.Add((float)Math.Sqrt(dx * dx + dy * dy + dz * dz) - r);
                }
            }
        }
        return grid;
    }

    static WorldRef PhysicsWorld(string key, float x)
    {
        var world = Phys2d.World(key, new WorldOpts { Gravity = new Vec2d { X = 0, Y = 0 }, FixedDt = 1.0f / 60.0f })!;
        Phys2d.Begin(world);
        var body = Phys2d.Body(world, "body", new BodyDesc { Type = Phys2d.BodyType.Static, Initial = new InitialState { X = x, Y = 0 } })!;
        Phys2d.Box(body, "box", new BoxDesc { Hx = 1, Hy = 1, Density = 0 });
        Phys2d.Step(world, 1.0f / 60.0f);
        return world;
    }

    static List<SdfNodeDesc> SdfOne(Mesh.SdfOp op, float[] ps)
    {
        var node = new SdfNodeDesc { Op = op, Params = new List<float>() };
        foreach (var p in ps)
            node.Params.Add(p);
        var nodes = new List<SdfNodeDesc>();
        nodes.Add(node);
        return nodes;
    }

    // frame 1: 同じ API を続けて呼び、前の view が前の中身のまま読めること
    static bool FirstFrame()
    {
        Io.LoadBytes(fontPath, out var ttf, out _, out _, out _);
        if (ttf == null)
        {
            Check.That(frame < 300, "font did not load: " + fontPath);
            return false;
        }

        var glyph = Font.Glyph(ttf, cpI, 32)!;
        var glyphBytes = CopyBytes(glyph.Bytes!);
        Font.Glyph(ttf, cpHo, 64);
        SameBytes(glyph.Bytes!, glyphBytes, "font.glyph: earlier bytes changed by the next call");
        heldBytes = glyph.Bytes;

        var mesh = Font.GlyphMesh(ttf, cpHo)!;
        var positions = Copy(mesh.Positions);
        var indices = CopyInts(mesh.Indices);
        Font.GlyphMesh(ttf, cpI);
        Font.GlyphMesh(ttf, cpHo, 0.0005f);
        Same(mesh.Positions, positions, "font.glyph_mesh positions");
        SameInts(mesh.Indices, indices, "font.glyph_mesh indices");
        heldPositions = mesh.Positions;

        var nets = Mesh.SurfaceNets(SphereGrid(8, 2.5f), 8, 8, 8);
        var netsPositions = Copy(nets.Positions);
        Mesh.SurfaceNets(SphereGrid(12, 4.5f), 12, 12, 12);
        Same(nets.Positions, netsPositions, "mesh.surface_nets positions");

        var small = Mesh.SdfMesh(SdfOne(Mesh.SdfOp.Sphere, new float[] { 0.3f }), 0, 16);
        var smallMin = Copy(small.BoundsMin!);
        var smallPositions = Copy(small.Positions);
        Mesh.SdfMesh(SdfOne(Mesh.SdfOp.Box, new float[] { 0.8f, 0.5f, 0.6f }), 0, 16);
        Same(small.BoundsMin!, smallMin, "mesh.sdf_mesh bounds_min");
        Same(small.Positions, smallPositions, "mesh.sdf_mesh positions");

        var left = PhysicsWorld("view_left", -3);
        var right = PhysicsWorld("view_right", 3);
        var dbg = Phys2d.Debug(left, new DebugOpts { Shapes = true })!;
        Check.That(dbg.Polygons.Count > 0, "phys2d debug: no polygons");
        var polygons = Copy(dbg.Polygons);
        Phys2d.Debug(right, new DebugOpts { Shapes = true, Bounds = true });
        Same(dbg.Polygons, polygons, "phys2d debug polygons");

        var path = "tests/cs/.lub/view_lifetime.txt";
        Io.SaveText(path, "first content");
        Io.LoadBytes(path, out var bytes, out _, out _, out _);
        Io.SaveText(path, "second content, longer than the first");
        Io.LoadBytes(path, out var replaced, out _, out _, out _);
        Check.That(replaced != null && bytes != null, "io.load_bytes: load missing");
        Check.That(Text(replaced!) == "second content, longer than the first", "io.load_bytes: reload missing");
        Check.That(Text(bytes!) == "first content", "io.load_bytes: earlier bytes changed by the reload");
        return true;
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        frame++;
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        if (heldBytes == null)
        {
            FirstFrame();
            return;
        }
        // frame 2 以降: 前の frame の view は読むと stale の error。期待するエラーは 1 回の callback に 1 つなので、読み方ごとに frame を分ける
        var s = step;
        step++;
        if (s == 0)
        {
            Check.Error("bytes index", "stale view");
            heldBytes.Get(0);
        }
        else if (s == 1)
        {
            Check.Error("bytes length", "stale view");
            var n = heldBytes.Length;
        }
        else if (s == 2)
        {
            Check.Error("positions index", "stale view");
            var p = heldPositions![0];
        }
        else
        {
            Check.Pass();
        }
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
