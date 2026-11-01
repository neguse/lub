// 同じ key の buffer は同じものを指す。
// 同じ version の宣言は同じ ref を返し、同じフレームの違う version は error になる。
// 次のフレームの違う version は編集なので作り直し、error で終わったフレームの次もそれができる。
// version を省いた宣言は内容が変わったという宣言で、同じフレームでも error にしない。
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
                    // 前のフレームは error で終わったが、別のフレームの宣言は編集。
                    var edited = Use(Data(), 4);
                    Check.That(edited != null && edited.Version == 4, "the next frame must accept a new version");
                    // data の無い宣言は問い合わせなので、外れても矛盾にしない。
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
