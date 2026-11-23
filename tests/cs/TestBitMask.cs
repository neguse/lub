// A bit mask string is 1 to 16 hex digits (a 0x prefix is allowed).
// Empty strings, 17 or more digits, and non-hex characters (including NUL) are errors (the tcs2c and .NET bindings follow the same rule).
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

        // Advance the stage before a call that should fail (when it fails, OnFrame ends there)
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
