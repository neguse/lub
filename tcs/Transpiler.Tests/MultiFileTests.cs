namespace TinyCs.Tests;

public class MultiFileTests
{
    [Fact]
    public void CrossFile_ClassReference()
    {
        var sources = new[]
        {
            """
            public class Vec2
            {
                public float X;
                public float Y;
                public Vec2(float x, float y)
                {
                    X = x;
                    Y = y;
                }
            }
            """,
            """
            public class T
            {
                public static float Test()
                {
                    var v = new Vec2(3.0f, 4.0f);
                    return v.X + v.Y;
                }
            }
            """
        };
        var lua = Transpiler.Transpile(sources);
        var script = $"{lua}\nprint(T.Test())";
        var result = TestHelper.RunLua(script).Trim();
        Assert.Equal("7.0", result);
    }

    [Fact]
    public void CrossFile_EnumReference()
    {
        var sources = new[]
        {
            """
            public enum Color { Red = 0, Green = 1, Blue = 2 }
            """,
            """
            public class T
            {
                public static int Test()
                {
                    return (int)Color.Blue;
                }
            }
            """
        };
        var lua = Transpiler.Transpile(sources);
        var script = $"{lua}\nprint(T.Test())";
        var result = TestHelper.RunLua(script).Trim();
        Assert.Equal("2", result);
    }

    [Fact]
    public void CrossFile_Inheritance()
    {
        var sources = new[]
        {
            """
            public class Animal
            {
                public string Name;
                public Animal(string name) { Name = name; }
                public string Speak() { return Name + " speaks"; }
            }
            """,
            """
            public class Dog : Animal
            {
                public Dog(string name) : base(name) { }
                public string Bark() { return Name + " barks"; }
            }
            """,
            """
            public class T
            {
                public static string Test()
                {
                    var d = new Dog("Rex");
                    return d.Bark();
                }
            }
            """
        };
        var lua = Transpiler.Transpile(sources);
        var script = $"{lua}\nprint(T.Test())";
        var result = TestHelper.RunLua(script).Trim();
        Assert.Equal("Rex barks", result);
    }

    [Fact]
    public void Namespace_FileScopedNamespace()
    {
        var source = """
            namespace Game;
            public class Player
            {
                public int Health = 100;
            }
            public class T
            {
                public static int Test()
                {
                    var p = new Player();
                    return p.Health;
                }
            }
            """;
        var lua = Transpiler.Transpile(source);
        var script = $"{lua}\nprint(T.Test())";
        var result = TestHelper.RunLua(script).Trim();
        Assert.Equal("100", result);
    }

    // ===== Reference sources (--ref) =====

    [Fact]
    public void Ref_TypeCheckOnly_NoLuaOutput()
    {
        // Reference source provides type info but no Lua output
        var refSource = """
            namespace Engine;
            public static class Gfx
            {
                public static int Width() => default!;
                public static int Height() => default!;
            }
            """;
        var mainSource = """
            using Engine;
            public class T
            {
                public static int Test()
                {
                    return Gfx.Width() + Gfx.Height();
                }
            }
            """;
        var result = Transpiler.TranspileWithDiagnostics(
            [mainSource], null, [refSource]);
        Assert.True(result.Success);
        // Only main source generates Lua — ref source does NOT
        Assert.Contains("T", result.Lua);
        Assert.DoesNotContain("Gfx = {}", result.Lua);
        Assert.DoesNotContain("Gfx.__index", result.Lua);
    }

    [Fact]
    public void Ref_EnumFromRefSource()
    {
        var refSource = """
            public enum LoadAction { DONT_CARE = 0, CLEAR = 1, LOAD = 2 }
            """;
        var mainSource = """
            public class T
            {
                public static int Test()
                {
                    return (int)LoadAction.CLEAR;
                }
            }
            """;
        var result = Transpiler.TranspileWithDiagnostics(
            [mainSource], null, [refSource]);
        Assert.True(result.Success);
        // Enum value should resolve correctly
        Assert.Contains("LoadAction.CLEAR", result.Lua);
        // But enum definition should NOT be emitted (from ref source)
        Assert.DoesNotContain("LoadAction = {", result.Lua);
    }

    [Fact]
    public void Namespace_ClassicNamespace()
    {
        var source = """
            namespace Game
            {
                public class Enemy
                {
                    public int Damage = 10;
                }
                public class T
                {
                    public static int Test()
                    {
                        var e = new Enemy();
                        return e.Damage;
                    }
                }
            }
            """;
        var lua = Transpiler.Transpile(source);
        var script = $"{lua}\nprint(T.Test())";
        var result = TestHelper.RunLua(script).Trim();
        Assert.Equal("10", result);
    }
}
