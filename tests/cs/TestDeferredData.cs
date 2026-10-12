// Use* / Snd の「同じ version なら data は読まない」規約。
// version が一致する宣言では data を変換せず、null でも通る。version が一致しない
// (または省略した) ときだけ data が要る。
// (Lua 版は不正な値 "not an array" を渡して「読まれたかどうか」を検出していたが、
// 型の違う値は C# では渡せないので移していない。)
using System;
using System.Collections.Generic;
using Lub;

public static class TestDeferredData
{
    static int step;
    static int waited;

    static readonly List<float> data = new List<float> { 1.0f, 2.0f, 3.0f, 4.0f };

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
    }

    static void Buffers()
    {
        var b1 = Gfx.UseBuffer("dd_buf", Gfx.BufferType.Storage, data, 1);
        Check.That(b1 != null && b1.Version == 1, "use_buffer: first declaration");
        var b3 = Gfx.UseBuffer("dd_buf", Gfx.BufferType.Storage, null, 1);
        Check.That(b3 != null, "use_buffer: matching version must accept nil data");
    }

    static void BuffersAfter()
    {
        // version を上げるときは data を渡す
        var b4 = Gfx.UseBuffer("dd_buf", Gfx.BufferType.Storage, data, 2);
        Check.That(b4 != null && b4.Version == 2, "use_buffer: version bump with data");

        var i1 = Gfx.UseBufferInts("dd_ints", Gfx.BufferType.Index, new List<int> { 0, 1, 2 }, 1);
        Check.That(i1 != null && i1.Version == 1, "use_buffer_ints: first declaration");
        var i3 = Gfx.UseBufferInts("dd_ints", Gfx.BufferType.Index, null, 1);
        Check.That(i3 != null, "use_buffer_ints: matching version must accept nil data");
    }

    static void Textures()
    {
        var px = new List<int> { 255, 0, 0, 255 };
        var t1 = Gfx.UseTexture("dd_tex", 1, 1, Gfx.PixelFormat.Rgba8, px, 1);
        Check.That(t1 != null && t1.Version == 1, "use_texture: first declaration");
        var t3 = Gfx.UseTexture("dd_tex", 1, 1, Gfx.PixelFormat.Rgba8, null, 1);
        Check.That(t3 != null, "use_texture: matching version must accept nil px");

        // px null は空 texture の宣言として mismatch でも通る (render target 等)
        var rt = Gfx.UseTexture("dd_rt", 1, 1, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true });
        Check.That(rt != null && rt.Version == 1, "use_texture: empty target texture");
        var blank = Gfx.UseTexture("dd_tex", 1, 1, Gfx.PixelFormat.Rgba8, null, 2);
        Check.That(blank != null && blank.Version == 2, "use_texture: nil px on mismatch declares an empty texture");
    }

    static bool Sounds()
    {
        var pcm = new List<float>();
        for (int i = 1; i <= 64; i++)
            pcm.Add((float)Math.Sin(i * 0.1f) * 0.5f);
        var s1 = Audio.Snd("dd_snd", pcm, 1, 48000, 1);
        Check.That(s1 != 0, "snd: first declaration");
        Check.That(Audio.Snd("dd_snd", null, 1, 48000, 1) == s1, "snd: matching version must accept nil data");
        return true;
    }

    // 保存した file が読めるまで同じ段階に留まる (上限を超えたら失敗)
    static void Wait(int stage)
    {
        step = stage;
        waited++;
        Check.That(waited < 300, "saved files did not become readable");
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        var s = step;
        step++;
        if (s == 0)
        {
            Buffers();
            // 一致しない version では data を読み、null は error
            Check.Error("use_buffer nil data on mismatch", "data");
            Gfx.UseBuffer("dd_buf", Gfx.BufferType.Storage, null, 2);
        }
        else if (s == 1)
        {
            Check.Error("use_buffer nil data, first declaration", "data");
            Gfx.UseBuffer("dd_buf_new", Gfx.BufferType.Storage, null, 1);
        }
        else if (s == 2)
        {
            // version を省くと常に「変わった」宣言なので data が要る
            Check.Error("use_buffer nil data without version", "data");
            Gfx.UseBuffer("dd_buf", Gfx.BufferType.Storage, null);
        }
        else if (s == 3)
        {
            BuffersAfter();
            Check.Error("use_buffer_ints nil data on mismatch", "data");
            Gfx.UseBufferInts("dd_ints", Gfx.BufferType.Index, null, 2);
        }
        else if (s == 4)
        {
            Textures();
            // key が別の種類で使われているときは data の有無によらず種類の error
            Check.Error("buffer over texture key, nil data", "different kind");
            Gfx.UseBuffer("dd_tex", Gfx.BufferType.Storage, null, 1);
        }
        else if (s == 5)
        {
            Check.Error("buffer over texture key, with data", "different kind");
            Gfx.UseBuffer("dd_tex", Gfx.BufferType.Storage, new List<float> { 1.0f }, 1);
        }
        else if (s == 6)
        {
            Check.Error("texture over buffer key, nil px", "different kind");
            Gfx.UseTexture("dd_buf", 1, 1, Gfx.PixelFormat.Rgba8, null, 1);
        }
        else if (s == 7)
        {
            Check.Error("texture over buffer key, with px", "different kind");
            Gfx.UseTexture("dd_buf", 1, 1, Gfx.PixelFormat.Rgba8, new List<int> { 0, 0, 0, 0 }, 1);
        }
        else if (s == 8)
        {
            Sounds();
            Check.Error("snd nil data on mismatch", "data");
            Audio.Snd("dd_snd", null, 1, 48000, 2);
        }
        else if (s == 9)
        {
            Check.Error("snd nil data, first declaration", "data");
            Audio.Snd("dd_snd_new", null, 1, 48000, 1);
        }
        else if (s == 10)
        {
            Check.Error("snd nil data without version", "data");
            Audio.Snd("dd_snd", null, 1, 48000);
        }
        else if (s == 11)
        {
            // snd_bytes の data は file から bytes で受ける。aligned.bin は
            // 4 の倍数の長さ (f32 PCM)、misaligned.bin は 4 の倍数でない長さ
            Io.SaveText("tests/cs/.lub/dd_aligned.bin", "abcdabcdabcdabcd");
            Io.SaveText("tests/cs/.lub/dd_misaligned.bin", "RIFFx");
        }
        else if (s == 12)
        {
            Io.LoadBytes("tests/cs/.lub/dd_aligned.bin", out var aligned, out _, out _, out _);
            Io.LoadBytes("tests/cs/.lub/dd_misaligned.bin", out var mis, out _, out _, out _);
            if (aligned == null || mis == null)
            {
                Wait(12);
                return;
            }
            var b1 = Audio.SndBytes("dd_bytes", aligned, 1, 48000, 1);
            Check.That(b1 != 0, "snd_bytes: first declaration");
            Check.That(Audio.SndBytes("dd_bytes", null, 1, 48000, 1) == b1, "snd_bytes: matching version must accept nil data");
            // 元のファイルの bytes (4 の倍数でない長さ) を渡しても、一致する間は読まれない
            Check.That(Audio.SndBytes("dd_bytes", mis, 1, 48000, 1) == b1, "snd_bytes: matching version must not check the length of data");
            Check.Error("snd_bytes nil data on mismatch", "data");
            Audio.SndBytes("dd_bytes", null, 1, 48000, 2);
        }
        else if (s == 13)
        {
            Io.LoadBytes("tests/cs/.lub/dd_misaligned.bin", out var mis, out _, out _, out _);
            if (mis == null)
            {
                Wait(13);
                return;
            }
            Check.Error("snd_bytes misaligned data on mismatch", "f32-aligned");
            Audio.SndBytes("dd_bytes", mis, 1, 48000, 2);
        }
        else
        {
            Io.LoadBytes("tests/cs/.lub/dd_aligned.bin", out var aligned, out _, out _, out _);
            if (aligned == null)
            {
                Wait(14);
                return;
            }
            Check.That(Audio.SndBytes("dd_bytes", aligned, 1, 48000, 2) != 0, "snd_bytes: version bump with data");
            Check.Pass();
        }
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
