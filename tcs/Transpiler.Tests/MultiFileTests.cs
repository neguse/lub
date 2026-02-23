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
