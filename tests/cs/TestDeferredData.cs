// The "data is not read if the version is the same" rule of Use* / Snd.
// A declaration whose version matches does not convert data, and passes even with null.
// A declaration that passes version and omits data returns null without changing anything if there is no resource with that version (not created, already swept, or a different version).
// When version is omitted, data is required.
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
        Check.That(Gfx.UseBuffer("dd_buf", Gfx.BufferType.Storage, null, 2) == null, "use_buffer: nil data on mismatch must be absent");
        Check.That(Gfx.UseBuffer("dd_buf_new", Gfx.BufferType.Storage, null, 1) == null, "use_buffer: nil data, first declaration must be absent");
        Check.That(Gfx.LookupBuffer("dd_buf_new") == null, "use_buffer: an absent declaration must not create the key");
        var kept = Gfx.UseBuffer("dd_buf", Gfx.BufferType.Storage, null, 1);
        Check.That(kept != null && kept.Version == 1, "use_buffer: an absent declaration must not change the stored version");
    }

    static void BuffersAfter()
    {
        // Pass data when raising the version
        var b4 = Gfx.UseBuffer("dd_buf", Gfx.BufferType.Storage, data, 2);
        Check.That(b4 != null && b4.Version == 2, "use_buffer: version bump with data");

        var i1 = Gfx.UseBufferInts("dd_ints", Gfx.BufferType.Index, new List<int> { 0, 1, 2 }, 1);
        Check.That(i1 != null && i1.Version == 1, "use_buffer_ints: first declaration");
        var i3 = Gfx.UseBufferInts("dd_ints", Gfx.BufferType.Index, null, 1);
        Check.That(i3 != null, "use_buffer_ints: matching version must accept nil data");
        Check.That(Gfx.UseBufferInts("dd_ints", Gfx.BufferType.Index, null, 2) == null, "use_buffer_ints: nil data on mismatch must be absent");
        Check.That(Gfx.UseBufferInts("dd_ints_new", Gfx.BufferType.Index, null, 1) == null, "use_buffer_ints: nil data, first declaration must be absent");
    }

    static void Textures()
    {
        var px = new List<int> { 255, 0, 0, 255 };
        var t1 = Gfx.UseTexture("dd_tex", 1, 1, Gfx.PixelFormat.Rgba8, px, 1);
        Check.That(t1 != null && t1.Version == 1, "use_texture: first declaration");
        var t3 = Gfx.UseTexture("dd_tex", 1, 1, Gfx.PixelFormat.Rgba8, null, 1);
        Check.That(t3 != null, "use_texture: matching version must accept nil px");

        // px null is a declaration of an empty texture and passes even on a mismatch (render targets etc.)
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
        Check.That(s1 != null, "snd: first declaration");
        Check.That(Audio.Snd("dd_snd", null, 1, 48000, 1) == s1, "snd: matching version must accept nil data");
        Check.That(Audio.Snd("dd_snd", null, 1, 48000, 2) == null, "snd: nil data on mismatch must be absent");
        Check.That(Audio.Snd("dd_snd_new", null, 1, 48000, 1) == null, "snd: nil data, first declaration must be absent");
        Check.That(Audio.Snd("dd_snd", null, 1, 48000, 1) == s1, "snd: an absent declaration must not change the stored version");
        return true;
    }

    // Stay at the same stage until the saved file can be read (fail if the limit is exceeded)
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
            // Omitting version always declares "changed", so data is required
            Check.Error("use_buffer nil data without version", "data");
            Gfx.UseBuffer("dd_buf", Gfx.BufferType.Storage, null);
        }
        else if (s == 1)
        {
            BuffersAfter();
            Textures();
            // When the key is used for a different kind, it is a kind error regardless of whether data is given
            Check.Error("buffer over texture key, nil data", "different kind");
            Gfx.UseBuffer("dd_tex", Gfx.BufferType.Storage, null, 1);
        }
        else if (s == 2)
        {
            Check.Error("buffer over texture key, with data", "different kind");
            Gfx.UseBuffer("dd_tex", Gfx.BufferType.Storage, new List<float> { 1.0f }, 1);
        }
        else if (s == 3)
        {
            Check.Error("texture over buffer key, nil px", "different kind");
            Gfx.UseTexture("dd_buf", 1, 1, Gfx.PixelFormat.Rgba8, null, 1);
        }
        else if (s == 4)
        {
            Check.Error("texture over buffer key, with px", "different kind");
            Gfx.UseTexture("dd_buf", 1, 1, Gfx.PixelFormat.Rgba8, new List<int> { 0, 0, 0, 0 }, 1);
        }
        else if (s == 5)
        {
            Sounds();
            Check.Error("snd nil data without version", "data");
            Audio.Snd("dd_snd", null, 1, 48000);
        }
        else if (s == 6)
        {
            // The data of Audio.SndBytes is received as bytes from a file.
            // aligned.bin has a length that is a multiple of 4 (f32 PCM); misaligned.bin has a length that is not a multiple of 4
            Io.SaveText("tests/cs/.lub/dd_aligned.bin", "abcdabcdabcdabcd");
            Io.SaveText("tests/cs/.lub/dd_misaligned.bin", "RIFFx");
        }
        else if (s == 7)
        {
            Io.LoadBytes("tests/cs/.lub/dd_aligned.bin", out var aligned, out _, out _, out _);
            Io.LoadBytes("tests/cs/.lub/dd_misaligned.bin", out var mis, out _, out _, out _);
            if (aligned == null || mis == null)
            {
                Wait(7);
                return;
            }
            var b1 = Audio.SndBytes("dd_bytes", aligned, 1, 48000, 1);
            Check.That(b1 != null, "snd_bytes: first declaration");
            Check.That(Audio.SndBytes("dd_bytes", null, 1, 48000, 1) == b1, "snd_bytes: matching version must accept nil data");
            // Even when the bytes of the original file (length not a multiple of 4) are passed, they are not read while the version matches
            Check.That(Audio.SndBytes("dd_bytes", mis, 1, 48000, 1) == b1, "snd_bytes: matching version must not check the length of data");
            Check.That(Audio.SndBytes("dd_bytes", null, 1, 48000, 2) == null, "snd_bytes: nil data on mismatch must be absent");
            Check.That(Audio.SndBytes("dd_bytes_new", null, 1, 48000, 1) == null, "snd_bytes: nil data, first declaration must be absent");
            Check.Error("snd_bytes nil data without version", "data");
            Audio.SndBytes("dd_bytes", null, 1, 48000);
        }
        else if (s == 8)
        {
            Io.LoadBytes("tests/cs/.lub/dd_misaligned.bin", out var mis, out _, out _, out _);
            if (mis == null)
            {
                Wait(8);
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
                Wait(9);
                return;
            }
            Check.That(Audio.SndBytes("dd_bytes", aligned, 1, 48000, 2) != null, "snd_bytes: version bump with data");
            Check.Pass();
        }
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
