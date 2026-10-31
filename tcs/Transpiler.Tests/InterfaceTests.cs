namespace TinyCs.Tests;

public class InterfaceTests
{
    [Fact]
    public void Interface_ClassImplementation()
    {
        var result = TestHelper.TranspileAndRun("""
            public interface IGreeter
            {
                string Greet(string name);
            }
            public class Hello : IGreeter
            {
                public string Greet(string name)
                {
                    return "Hello, " + name;
                }
            }
            public class T
            {
                public static string Test()
                {
                    var h = new Hello();
                    return h.Greet("world");
                }
            }
            """,
            "T.Test()");
        Assert.Equal("Hello, world", result);
    }

    [Fact]
    public void Interface_MultipleInterfaces()
    {
        var result = TestHelper.TranspileAndRun("""
            public interface IMovable
            {
                int Speed { get; }
            }
            public interface IDamageable
            {
                int Health { get; }
            }
            public class Player : IMovable, IDamageable
            {
                public int Speed { get; } = 10;
                public int Health { get; } = 100;
            }
            public class T
            {
                public static int Test()
                {
                    var p = new Player();
                    return p.Speed + p.Health;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("110", result);
    }

    // module mode でも interface 経由の property は accessor を通り、
    // 自動 property の accessor は本文の差し替えの後も残る
    [Fact]
    public void InterfaceProperty_ModuleMode()
    {
        const string source = """
            public interface IEdit { int W { get; set; } }
            public class Auto : IEdit { public int W { get; set; } = 1; }
            public class Custom : IEdit { int k = 2; public int W { get { return k * 10; } set { k = value; } } }
            public class T
            {
                public static string Run()
                {
                    IEdit a = new Auto();
                    IEdit c = new Custom();
                    a.W = 3;
                    c.W = 4;
                    return a.W + ":" + c.W;
                }
            }
            """;
        var session = new IncrementalCompilationSession();
        session.OpenProject([("a.cs", source)]);
        Assert.Equal(["3:40"], StaticInitOrderTests.RunSnapshot(
            StaticInitOrderTests.Snapshot(session), """print(reg.types["a.cs#T"].Run())"""));
        var result = session.Update("a.cs", source.Replace("k * 10", "k * 100"));
        Assert.True(result.FastPath);
        Assert.Equal(["3:400"], StaticInitOrderTests.RunSnapshot(
            StaticInitOrderTests.Snapshot(session), """print(reg.types["a.cs#T"].Run())"""));
    }

    // module mode の cast は runtime の table (TinySystem.cast など) を通る
    [Fact]
    public void Casts_ModuleMode()
    {
        const string source = """
            public interface IShape { }
            public class Sq : IShape { }
            public class Plain { }
            public class T
            {
                static object Sq1() { return new Sq(); }
                public static string Run()
                {
                    var sq = (Sq)Sq1();
                    var shape = (IShape)Sq1();
                    return (sq != null) + ":" + (shape != null) + ":" + (string)Str() + ":" + (int)One() + ":" + (float)Half() + ":" + (bool)Yes();
                }
                static object Str() { return "s"; }
                static object One() { return 1; }
                static object Half() { return 0.5f; }
                static object Yes() { return true; }
                public static object Bad(int kind)
                {
                    object o = new Plain();
                    if (kind == 0) return (Sq)o;
                    if (kind == 1) return (IShape)o;
                    if (kind == 2) return (string)o;
                    if (kind == 3) return (int)o;
                    return (bool)o;
                }
            }
            """;
        var session = new IncrementalCompilationSession();
        session.OpenProject([("a.cs", source)]);
        Assert.Equal(["True:True:s:1:0.5:True", "false", "false", "false", "false", "false"],
            StaticInitOrderTests.RunSnapshot(StaticInitOrderTests.Snapshot(session), """
                local t = reg.types["a.cs#T"]
                print(t.Run())
                for kind = 0, 4 do print((pcall(t.Bad, kind))) end
                """));
    }

    // module mode の interface の検査は runtime の table (TinySystem.implements) を通る。
    // __interfaces は型の定義の key で、module の登録と本文の差し替えで残る
    [Fact]
    public void InterfaceTypeTest_ModuleMode()
    {
        const string source = """
            public interface IShape { }
            public class Sq : IShape { public static int V() { return 1; } }
            public record Pt(int X) : IShape;
            public class Plain { }
            public class T
            {
                public static string Run()
                {
                    object a = new Sq();
                    object b = new Pt(1);
                    object c = new Plain();
                    return (a is IShape) + ":" + (b as IShape != null) + ":" + (c is IShape) + ":" + Sq.V();
                }
            }
            """;
        var session = new IncrementalCompilationSession();
        session.OpenProject([("a.cs", source)]);
        Assert.Equal(["True:True:False:1"], StaticInitOrderTests.RunSnapshot(
            StaticInitOrderTests.Snapshot(session), """print(reg.types["a.cs#T"].Run())"""));
        var result = session.Update("a.cs", source.Replace("return 1;", "return 2;"));
        Assert.True(result.FastPath);
        Assert.Equal(["True:True:False:2"], StaticInitOrderTests.RunSnapshot(
            StaticInitOrderTests.Snapshot(session), """print(reg.types["a.cs#T"].Run())"""));
    }
}
