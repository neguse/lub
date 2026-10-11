using System;
using System.Collections.Generic;
using Lub;

class BindingTest
{
    public static void Main()
    {
        Console.WriteLine(Gfx.MainTex.Version);
        var texture = Gfx.UseTexture("image", 1, 1, Gfx.PixelFormat.Rgba8,
            new List<int> { 255, 128, 0, 255 }, 7, new TextureOpts { Target = true });
        Console.WriteLine(texture.Version);
        Gfx.Draw(3, new Dictionary<string, object> {
            ["image"] = texture,
            ["uniforms"] = new Dictionary<string, object> {
                ["color"] = new float[] { 1, 2, 3, 4 }, ["scale"] = new List<float> { 5, 6 }
            }
        }, new DrawOpts { Depth = false });
        var view = Xr.View(0, .05f, 500);
        Console.WriteLine(view.Width);
        Console.WriteLine(view.ViewProjection[15]);
        Console.WriteLine(Xr.View(1, .05f, 500) == null);
        Console.WriteLine(view.Target != null);
        Audio.Voice("voice", 12, new VoiceOpts { Volume = .25f, Loop = true });
        Io.LoadBytes("data", out var bytes, out _, out _, out _);
        Console.WriteLine(Audio.SndBytes("sound", bytes, 1, 48000));
        Console.WriteLine(Audio.Info().Rate);
        var mesh = Mesh.SdfMesh(new List<SdfNodeDesc> {
            new SdfNodeDesc { Op = Mesh.SdfOp.Sphere, Params = new List<float> { 1, 2 }, Name = "root" }
        }, 0, 8);
        Console.WriteLine(mesh.Positions.Count);
        Console.WriteLine(mesh.Bones![0].Name);
        Console.WriteLine(mesh.Uvs == null);
        var packed = Io.InterleavePncm(mesh);
        Console.WriteLine(packed.Count);
        Console.WriteLine(packed[1]);
        Io.LoadFloats("floats", out var floats, out _, out _, out _);
        Console.WriteLine(floats == null);
        Console.WriteLine(Font.Glyph(bytes, 65, 12) == null);
        var glyph = Font.Glyph(bytes, 66, 12);
        Console.WriteLine(glyph.Bytes.Get(1));
        Console.WriteLine(glyph.W);
        var world = Phys2d.World("world", new WorldOpts { Substeps = 4 })!;
        var angle = Phys2d.JointAngle(Phys2d.FindJoint(world, "j")!);
        Console.WriteLine(angle != null ? angle.Value : -1f);
        Console.WriteLine(Phys2d.JointAngle(Phys2d.FindJoint(world, "none")!) == null);
        Io.LoadGltf("none", out var missing, out _, out _, out _);
        Console.WriteLine(missing == null);
        Io.LoadGltf("a.glb", out var gltf, out _, out _, out _);
        Console.WriteLine(gltf!.VertCount);
        Console.WriteLine(gltf.Material == null);
        var visits = 0;
        var bits = "";
        var query = new RaycastDesc { Dx = 1, Filter = new FilterDesc { CategoryBits = "0x1F" } };
        var hits = Phys2d.RaycastAll(world, query, hit =>
        {
            visits++;
            bits = hit.CategoryBits ?? "none";
            return hit.Fraction;
        });
        Console.WriteLine(visits);
        Console.WriteLine(hits.Count);
        Console.WriteLine(bits);
    }
}
