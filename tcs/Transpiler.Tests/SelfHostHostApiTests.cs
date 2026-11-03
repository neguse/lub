namespace TinyCs.Tests;

/// <summary>
/// セルフホストの compiler の host (--ref) の規則: 型は小文字のパス (lub.gfx)、member は snake_case、
/// enum の member は UPPER_SNAKE (値は host が持つので畳まない)、property は field、instance の method は `:`、
/// data class の new は table、out は multi-return、host の data class の継承と属性の宣言。
/// 偽の host (Lua の table) の上で参照実装の出力と同じ結果になるかを比べる。lub の samples と tests/cs は lub の上で同じ結果になることを確かめた
/// (doc/self-hosting-design.md §17.7)。
/// </summary>
public class SelfHostHostApiTests
{
    private const string Stub = """
        using System;
        namespace Lub
        {
            [AttributeUsage(AttributeTargets.Class)]
            public sealed class LubHandleAttribute : Attribute { }
            public class BaseView { public int Count; public int Size() { return 0; } }
            public class MeshView : BaseView { public float[] Positions; public int VertCount { get; } }
            public class Opts { public int MaxCount; public bool DepthTest = true; }
            [LubHandle] public sealed class Tex { }
            public static class Gfx
            {
                public enum Fmt { Rgba8 = 1, Depth24Stencil8 = 2 }
                public static Tex MainTex;
                public static Tex UseTexture(string key, Fmt fmt, Opts? opts = null) { return null; }
                public static MeshView Load(string key, out int version, out string err) { version = 0; err = null; return null; }
                public static int Width { get; }
                public static string Name(Fmt f) { return ""; }
            }
            public enum EventKind { Quit = 1, KeyDown = 2 }
        }
        """;

    private const string Program = """
        using Lub;
        public static class P
        {
            public static string Main()
            {
                var t = Gfx.UseTexture("a", Gfx.Fmt.Depth24Stencil8, new Opts { MaxCount = 3 });
                var m = Gfx.Load("k", out var v, out var err);
                BaseView b = m;
                int n = m.VertCount + m.Count + b.Size() + m.Size() + Gfx.Width + v;
                var o = new Opts();
                o.DepthTest = false;
                if (Gfx.MainTex == t) n++;
                string kind = EventKind.KeyDown switch { EventKind.Quit => "quit", _ => "other" };
                switch (Gfx.Fmt.Rgba8)
                {
                    case Gfx.Fmt.Rgba8: kind += ":rgba"; break;
                    default: kind += ":?"; break;
                }
                return n + ":" + err + ":" + kind + ":" + m.Positions.Length + ":" + Gfx.Name(Gfx.Fmt.Rgba8) + ":" + (o.DepthTest ? 1 : 0);
            }
        }
        """;

    // 偽の host。enum の値は host の値 (数でないものもある) で、C# の宣言の値とは違う
    private const string Host = """
        local tex = {}
        local mesh = { count = 4, vert_count = 3, positions = {1, 2, 3, 4, 5} }
        function mesh:size() return 10 end
        lub = {
          KEY_DOWN = "keydown", QUIT = "quit",
          gfx = {
            RGBA8 = "rgba8", DEPTH24_STENCIL8 = "d24s8", main_tex = tex, width = 100,
            use_texture = function(key, fmt, opts) assert(fmt == "d24s8" and opts.max_count == 3); return tex end,
            load = function(key) return mesh, 7, "none" end,
            name = function(f) return "fmt=" .. f end,
          },
        }
        """;

    [Fact]
    public void HostConventions_MatchReference()
    {
        var reference = Transpiler.TranspileWithDiagnostics([Program], referenceSources: [Stub]);
        Assert.True(reference.Success, string.Join("\n", reference.Errors));
        var referenceOut = TestHelper.RunLua(LuaRuntime.EmbedRuntime(reference.Lua).Lua + "\n" + Host + "\nprint(P.Main())").Trim();

        var self = TcsSelf.Compiler.Compile(["p.cs"], [Program], ["stub.cs"], [Stub], "");
        Assert.True(self.Ok, string.Join("\n", self.Diagnostics));
        var prelude = LuaRuntime.EmbedRuntime(Transpiler.Transpile("// empty")).Lua;
        prelude = prelude[..prelude.LastIndexOf("-- tcs:type-locals", StringComparison.Ordinal)];
        var selfOut = TestHelper.RunLua(prelude + self.Lua + "\n" + Host + "\nprint(P.Main())").Trim();

        Assert.Equal("135:none:other:rgba:5:fmt=rgba8:0", referenceOut);
        Assert.Equal(referenceOut, selfOut);
    }
}
