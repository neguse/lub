// bit mask の文字列は 1〜16 桁の hex (0x 付きも可)。
// 空、17 桁以上、hex でない文字 (NUL を含む) は error (tcs2c と .NET の binding も同じ規則)。
using System;
using Lub;

public static class TestBitMask
{
    static int step;
    static WorldRef? world;

    static readonly string[] badMasks = new string[] { "", "0x", "1ffffffffffffffff", "0x00000000000000001", "12g", "1\0zz", "1\0" };
    static readonly string[] badWants = new string[] { "empty", "empty", "16 hex digits", "16 hex digits", "invalid hex", "invalid hex", "invalid hex" };

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 64, Height = 64 });
    }

    static RayHit? Ray(string mask)
    {
        return Phys2d.Raycast(world!, new RaycastDesc { X = 0, Y = 0, Dx = 2, Dy = 0, Filter = new FilterDesc { MaskBits = mask } });
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        world = Phys2d.World("bits", new WorldOpts { Gravity = new Vec2d { X = 0, Y = 0 } });
        Phys2d.Begin(world!);
        var body = Phys2d.Body(world!, "box", new BodyDesc { Type = Phys2d.BodyType.Static, Initial = new InitialState { X = 1, Y = 0 } });
        Phys2d.Box(body!, "box", new BoxDesc { Hx = 0.2f, Hy = 0.2f });
        Phys2d.Step(world!, 1.0f / 60.0f);

        // 段階は失敗するはずの呼び出しの前に進める (失敗すると OnFrame はそこで終わる)
        var s = step;
        step++;
        if (s == 0)
        {
            Check.That(Ray("1") != null && Ray("0x1") != null && Ray("ffffffffffffffff") != null, "valid mask missed the box");
            Check.That(Ray("0") == null && Ray("0x0000000000000000") == null, "mask 0 hit the box");
        }
        else if (s <= badMasks.Length)
        {
            var i = s - 1;
            Check.Error("mask '" + badMasks[i] + "'", badWants[i]);
            Ray(badMasks[i]);
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
