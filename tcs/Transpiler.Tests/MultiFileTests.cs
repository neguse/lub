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
        var result = TestHelper.CompileAndRunVm(sources, "T.Test()");
        Assert.Equal("7", result);
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
        var result = TestHelper.CompileAndRunVm(sources, "T.Test()");
        Assert.Equal("2", result);
    }

    [Fact]
    public void CrossFile_Composition()
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
            public class Dog
            {
                public Animal Body;
                public Dog(string name) { Body = new Animal(name); }
                public string Bark() { return Body.Name + " barks"; }
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
        var result = TestHelper.CompileAndRunVm(sources, "T.Test()");
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
        var result = TestHelper.CompileAndRunVm(source, "Game.T.Test()");
        Assert.Equal("100", result);
    }

    // ===== Reference sources (--ref) =====

    [Fact]
    public void Ref_TypeCheckOnly_NotExported()
    {
        // Reference source provides type info but is not exported (calls go to the host)
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
        var result = IlExport.Export([mainSource], referenceSources: [refSource]);
        Assert.Empty(result.Diagnostics);
        Assert.Contains(result.Classes, c => c.Name == "T");
        Assert.DoesNotContain(result.Classes, c => c.Name.Contains("Gfx"));
        Assert.Equal(["engine.gfx.height", "engine.gfx.width"],
            result.ForeignMethods.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal));
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
        // Enum value should resolve correctly
        var output = VmCorpus.RunPrograms([mainSource,
            "public static class TcsEntry { public static void Main() { System.Console.WriteLine(T.Test()); } }"],
            "TcsEntry", refs: [refSource]);
        Assert.Equal("1", output);
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
        var result = TestHelper.CompileAndRunVm(source, "Game.T.Test()");
        Assert.Equal("10", result);
    }
}
