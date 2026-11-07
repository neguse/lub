// lubx の部品は key を最初の引数に取り、resource をその key か key + "/名前" に宣言する。
// 1 つのアプリで同じ部品を 2 つのパスで使っても衝突せず、同じパスなら同じ resource を指すことを見る。
// SpriteBatch の shader だけは中身が定数なので、パスによらず 1 つ (lubx/sprite/instanced) を共有する。
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

// 1 つのパスの下に部品を一通り持つゲーム
public class TestLubxKeysGame
{
    public readonly string Root;
    public readonly string Fs;
    public readonly int Snd;
    public readonly Mesh3d Mesh;
    public readonly Atlas Atlas;
    readonly SpriteBatch batch;
    readonly Text text;
    readonly MeshText meshText;
    readonly Renderer3d ren;
    readonly Camera cam = new Camera { Eye = new Vec3(0, 1.5f, -4), Target = new Vec3(0, 0, 0) };

    public TestLubxKeysGame(string root, string fs, string font, float gain)
    {
        Root = root;
        Fs = fs;
        Snd = Sfx.Synth(root + "/sfx", 0.01f, 1, (t, u) => gain);
        Mesh = new Mesh3d(root + "/mesh");
        Mesh.Rebuild(Shapes3d.Cube());
        var pixels = new List<int>();
        for (int i = 0; i < 2 * 2 * 4; i++)
            pixels.Add(255);
        Atlas = Atlas.FromPixels(root + "/atlas", 2, 2, pixels);
        batch = new SpriteBatch(root + "/batch", 320, 180);
        text = new Text(root + "/text", font, 16, 32);
        meshText = new MeshText(root + "/mesh_text", font, 1, 320, 180);
        ren = new Renderer3d(root + "/ren");
    }

    public void Declare(string vsPath)
    {
        Assets.Shader(Root + "/shader", vsPath, Fs);
        Assets.RenderTarget(Root + "/rt", 8, 8, Gfx.PixelFormat.Rgba8);
        ren.Begin(cam);
        ren.Draw(Mesh, Mat4.Identity());
        ren.End();
        batch.Begin();
        batch.Quad(Atlas, new Rect(0, 0, 2, 2), 0, 0, 2, 2);
        text.Draw(batch, "A", 0, 20);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        batch.Flush();
        meshText.Text("A", 10, 100, 48);
        Gfx.EndPass();
    }

    // 各部品が宣言する resource のうち代表を引き、生きている数を返す
    public int Live()
    {
        int n = 0;
        foreach (var name in new List<string> { "/shader", "/mesh_text/shader", "/ren/lit_s" })
            n += Gfx.LookupShader(Root + name) != null ? 1 : 0;
        foreach (var name in new List<string> { "/batch/quad", "/mesh/vb", "/mesh/ib", "/mesh_text/v:65", "/ren/quad" })
            n += Gfx.LookupBuffer(Root + name) != null ? 1 : 0;
        foreach (var name in new List<string> { "/atlas", "/text", "/rt", "/ren/hdr" })
            n += Gfx.LookupTexture(Root + name) != null ? 1 : 0;
        return n;
    }
}

public static class TestLubxKeys
{
    const int sweepFrames = 2;
    const int allResources = 12;
    const string fontPath = "samples/data/fonts/MPLUS1p-subset.ttf";
    const string vsPath = "tests/cs/data/test_dup_binding.vs.slang";
    const string fsPathA = "tests/cs/data/test_dup_binding.fs.slang";
    const string fsPathB = "tests/cs/data/test_buffer_capacity.fs.slang";

    static int frame;
    static TestLubxKeysGame? a;
    static TestLubxKeysGame? b;

    public static void OnInit()
    {
        App.Config(new ConfigOpts
        {
            Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu",
            Width = 320,
            Height = 180,
            ResourceSweepAfterFrames = sweepFrames,
        });
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        Io.LoadBytes(fontPath, out var ttf, out _, out _, out _);
        if (ttf == null || Assets.Shader("game_a/shader", vsPath, fsPathA) == null
            || Assets.Shader("game_b/shader", vsPath, fsPathB) == null)
            return;
        frame++;

        if (frame == 1)
        {
            a = new TestLubxKeysGame("game_a", fsPathA, fontPath, 0.25f);
            b = new TestLubxKeysGame("game_b", fsPathB, fontPath, 0.5f);
            Check.That(a.Snd != b.Snd, "two paths must not share a snd");
            a.Declare(vsPath);
            b.Declare(vsPath);
            Check.That(a.Live() == allResources, "game_a declares everything under its path: " + a.Live());
            Check.That(b.Live() == allResources, "game_b declares everything under its path: " + b.Live());
            Check.That(Gfx.LookupShader("lubx/sprite/instanced") != null && Gfx.LookupShader("game_a/batch/instanced") == null,
                "the sprite batches of both paths must share one shader");
        }
        else if (frame == 2)
        {
            // 同じパスの部品を作り直すと、同じ resource が更新され、他のパスは変わらない
            var oldMesh = a!.Mesh.Vb!.Version;
            var oldAtlas = a.Atlas.Texture!.Version;
            var otherMesh = b!.Mesh.Vb!.Version;
            var otherAtlas = b.Atlas.Texture!.Version;
            b.Declare(vsPath);
            var snd = a.Snd;
            a = new TestLubxKeysGame("game_a", fsPathA, fontPath, 0.25f);
            a.Declare(vsPath);
            Check.That(a.Snd == snd, "the same path must return the same snd");
            Check.That(a.Mesh.Vb!.Version != oldMesh && Gfx.LookupBuffer("game_a/mesh/vb")!.Version == a.Mesh.Vb.Version, "the same path must update the same mesh");
            Check.That(a.Atlas.Texture!.Version != oldAtlas && Gfx.LookupTexture("game_a/atlas")!.Version == a.Atlas.Texture.Version, "the same path must update the same atlas");
            Check.That(Gfx.LookupBuffer("game_b/mesh/vb")!.Version == otherMesh, "another path must keep its mesh");
            Check.That(Gfx.LookupTexture("game_b/atlas")!.Version == otherAtlas, "another path must keep its atlas");
        }
        else if (frame < 3 + sweepFrames + 1)
        {
            // game_b だけ宣言をやめる
            a!.Declare(vsPath);
        }
        else if (frame == 3 + sweepFrames + 1)
        {
            a!.Declare(vsPath);
            Check.That(a.Live() == allResources, "the path still declared must stay live: " + a.Live());
            Check.That(b!.Live() == 1, "the path no longer declared must be swept, except its shader declared above: " + b.Live());
            Check.Error("two shaders on the same path must conflict", "different versions");
            Assets.Shader("game_a/shader", vsPath, fsPathB);
        }
        else
        {
            Check.Pass();
        }
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
