namespace TinyCs.Tests;

public class ClassTests
{
    [Fact]
    public void Constructor_NoArgs()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Counter
            {
                public int Count = 0;

                public int GetCount() { return this.Count; }
            }
            """,
            "Counter.new():GetCount()");
        Assert.Equal("0", result);
    }

    [Fact]
    public void Constructor_WithArgs()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Point
            {
                public int X;
                public int Y;

                public Point(int x, int y)
                {
                    this.X = x;
                    this.Y = y;
                }

                public int Sum() { return this.X + this.Y; }
            }
            """,
            "Point.new(3, 4):Sum()");
        Assert.Equal("7", result);
    }

    [Fact]
    public void InstanceMethod_ModifiesState()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Counter
            {
                public int Count = 0;

                public void Increment()
                {
                    this.Count = this.Count + 1;
                }

                public int GetCount() { return this.Count; }
            }
            """, """
            (function()
              local c = Counter.new()
              c:Increment()
              c:Increment()
              c:Increment()
              return c:GetCount()
            end)()
            """);
        Assert.Equal("3", result);
    }

    [Fact]
    public void AutoProperty_AsField()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Item
            {
                public string Name { get; set; }
                public int Value { get; set; }

                public Item(string name, int value)
                {
                    this.Name = name;
                    this.Value = value;
                }

                public string Describe()
                {
                    return this.Name;
                }
            }
            """,
            "Item.new('sword', 100):Describe()");
        Assert.Equal("sword", result);
    }

    [Fact]
    public void FieldInitializer()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Config
            {
                public int MaxHP = 100;
                public int Speed = 5;

                public int Total() { return this.MaxHP + this.Speed; }
            }
            """,
            "Config.new():Total()");
        Assert.Equal("105", result);
    }

    [Fact]
    public void InstanceField_DefaultValues()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Defaults
            {
                public int Count;
                public bool Enabled;
                public string Name;
            }
            """, """
            (function()
              local d = Defaults.new()
              return tostring(d.Count) .. "," .. tostring(d.Enabled) .. "," .. tostring(d.Name == nil)
            end)()
            """);

        Assert.Equal("0,false,true", result);
    }

    [Fact]
    public void AutoProperty_DefaultValues()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Defaults
            {
                public int Count { get; set; }
                public bool Enabled { get; set; }
                public string Name { get; set; }
            }
            """, """
            (function()
              local d = Defaults.new()
              return tostring(d.Count) .. "," .. tostring(d.Enabled) .. "," .. tostring(d.Name == nil)
            end)()
            """);

        Assert.Equal("0,false,true", result);
    }

    [Fact]
    public void ExpressionBodiedMethod()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Calc
            {
                public static int Square(int x) => x * x;
            }
            """,
            "Calc.Square(7)");
        Assert.Equal("49", result);
    }

    [Fact]
    public void MultipleClasses()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Vec2
            {
                public int X;
                public int Y;
                public Vec2(int x, int y) { this.X = x; this.Y = y; }
            }

            public class Physics
            {
                public static int Distance(Vec2 a, Vec2 b)
                {
                    var dx = a.X - b.X;
                    var dy = a.Y - b.Y;
                    return dx * dx + dy * dy;
                }
            }
            """, """
            Physics.Distance(Vec2.new(1, 2), Vec2.new(4, 6))
            """);
        Assert.Equal("25", result);
    }

    // ===== Static fields =====

    [Fact]
    public void StaticField_DefaultValue()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Game
            {
                private static int counter;

                public static int GetCounter() { return counter; }
            }
            """,
            "Game.GetCounter()");
        Assert.Equal("0", result);
    }

    [Fact]
    public void StaticField_WithInitializer()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Game
            {
                private static int counter = 42;

                public static int GetCounter() { return counter; }
            }
            """,
            "Game.GetCounter()");
        Assert.Equal("42", result);
    }

    [Fact]
    public void StaticField_ReadWrite()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Game
            {
                private static float timer;

                public static void Update(float dt)
                {
                    timer = timer + dt;
                }

                public static float GetTimer() { return timer; }
            }
            """, """
            (function() Game.Update(1.5); Game.Update(2.5); return Game.GetTimer() end)()
            """);
        Assert.Equal("4.0", result);
    }

    [Fact]
    public void StaticField_Const()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Config
            {
                public const int MaxHP = 100;
                public const float Speed = 5.5f;

                public static float GetMaxSpeed() { return Speed; }
            }
            """,
            "Config.MaxHP + Config.GetMaxSpeed()");
        Assert.Equal("105.5", result);
    }

    [Fact]
    public void StaticField_MixedWithInstanceFields()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Entity
            {
                private static int nextId;
                public int Id;
                public string Name;

                public Entity(string name)
                {
                    nextId = nextId + 1;
                    Id = nextId;
                    Name = name;
                }
            }
            """, """
            (function() local a = Entity.new("Alice"); local b = Entity.new("Bob"); return tostring(a.Id) .. "," .. tostring(b.Id) end)()
            """);
        Assert.Equal("1,2", result);
    }

    [Fact]
    public void OptionalParameters_FillDefaultsWhenOmitted()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public T(int seed = 7)
                {
                    Seed = seed;
                }

                public int Seed;

                public static string F(int x, int y = -1, string tag = "none")
                    => x + "," + y + "," + tag;

                public static string Test()
                    => F(5) + "|" + F(1, 2, "yes") + "|" + new T().Seed;
            }
            """,
            "T.Test()");
        Assert.Equal("5,-1,none|1,2,yes|7", result);
    }

    [Fact]
    public void StaticFieldInitializers_SeeDefaultValuesOfLaterFields()
    {
        // C# は static field を default 値で事前初期化してから initializer を
        // 宣言順に実行する (循環参照でも nil にならない)
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                static int a = b + 1;
                static int b = a + 1;

                public static string Test() => a + "," + b;
            }
            """,
            "T.Test()");
        Assert.Equal("1,2", result);
    }
}
