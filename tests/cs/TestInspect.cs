// Sys.Inspect: overlay があるとき、--trace で変換したゲームの class から辿れる値の木を 1 段ずつ返す。
// lub tests/cs/TestInspect.csproj で動かす (overlay は TestInspectTool)。
using System;
using System.Collections.Generic;
using Lub;

public class Crate
{
    public int Hp;
    public string Name = "";
    public Crate? Next;
}

// TestInspectTool が覚えてから手放す instance と、回収を促すごみ
public static class Bin
{
    public static Crate? Held = new Crate();
    public static Crate? Junk;
}

public static class TestInspect
{
    public static int Frames;
    static Crate? first;
    static List<Crate> crates = new List<Crate>();
    static Dictionary<string, Crate> named = new Dictionary<string, Crate>();
    static string? missing;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "vulkan" });
    }

    public static void OnFrame(float dt)
    {
        Frames++;
        for (var i = 0; Bin.Held == null && i < 1000; i++)
            Bin.Junk = new Crate();
        if (first != null)
            return;
        first = new Crate { Hp = 3, Name = "a" };
        first.Next = new Crate { Hp = 5, Name = "b", Next = first };
        crates.Add(first);
        crates.Add(first.Next);
        crates.Add(new Crate { Hp = 9 });
        named["big one"] = new Crate { Hp = 1 };
        named["small"] = new Crate { Hp = 2 };
    }
}
