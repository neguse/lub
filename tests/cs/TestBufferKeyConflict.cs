// Buffers with the same key refer to the same thing.
// A declaration with the same version returns the same ref, and a different version in the same frame is an error.
// A different version in the next frame is an edit, so it is recreated; this also works in the frame after one that ended with an error.
// A declaration that omits version declares that the contents changed, and is not an error even in the same frame.
// A declaration without data is a declaration that returns the same ref on a match, and a query that does nothing on a miss.
// A match also counts as a declaration, so the form `Use(null, v) ?? Use(data, v)` gives the same result whether the key is empty or still present.
using System;
using System.Collections.Generic;
using Lub;

public static class TestBufferKeyConflict
{
    static int frame;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 64, Height = 64 });
    }

    static List<float> Data()
    {
        return new List<float> { 1, 2, 3, 4 };
    }

    static BufferRef? Use(List<float>? data, int version)
    {
        return Gfx.UseBuffer("conflict_buf", Gfx.BufferType.Storage, data, version);
    }

    static BufferRef? Declare(string key, int version)
    {
        return Gfx.UseBuffer(key, Gfx.BufferType.Storage, null, version)
            ?? Gfx.UseBuffer(key, Gfx.BufferType.Storage, Data(), version);
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        frame++;
        switch (frame)
        {
            case 1:
            case 2:
                {
                    var first = Use(Data(), frame);
                    var second = Use(Data(), frame);
                    var third = Use(null, frame);
                    Check.That(first != null && first.Version == frame && second != null && second.Version == frame && third != null,
                        "the same version must return the same buffer");
                    break;
                }
            case 3:
                Use(Data(), 3);
                Check.Error("a different version in the same frame must fail", "different versions");
                Use(Data(), 103);
                break;
            case 4:
                {
                    // The previous frame ended with an error, but a declaration in a different frame is an edit.
                    var edited = Use(Data(), 4);
                    Check.That(edited != null && edited.Version == 4, "the next frame must accept a new version");
                    // A declaration without data is a query, so a miss is not a conflict.
                    Check.That(Use(null, 104) == null, "a different version without data must be absent");
                    var kept = Use(null, 4);
                    Check.That(kept != null && kept.Version == 4, "a miss must not change the stored version");
                    break;
                }
            case 5:
                {
                    var a = Gfx.UseBuffer("changed_buf", Gfx.BufferType.Storage, Data());
                    var b = Gfx.UseBuffer("changed_buf", Gfx.BufferType.Storage, Data());
                    Check.That(a != null && b != null, "declarations without a version must not conflict");
                    Gfx.UseBufferEmpty("empty_buf", Gfx.BufferType.Storage, 4, 1);
                    Check.Error("use_buffer_empty follows the same rule", "different versions");
                    Gfx.UseBufferEmpty("empty_buf", Gfx.BufferType.Storage, 4, 2);
                    break;
                }
            case 6:
                // The key is empty: the first one misses and declares with data.
                Check.That(Declare("repeat_buf", 1) != null, "the first declaration must succeed");
                Check.Error("two versions on an empty key must fail", "different versions");
                Declare("repeat_buf", 2);
                break;
            case 7:
                // Version 1 remains in the key: the first one matches without data.
                Check.That(Declare("repeat_buf", 1) != null, "the matching declaration must succeed");
                Check.Error("two versions on a kept key must fail the same way", "different versions");
                Declare("repeat_buf", 2);
                break;
            default:
                Check.Pass();
                break;
        }
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
