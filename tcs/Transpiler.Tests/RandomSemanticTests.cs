namespace TinyCs.Tests;

public class RandomSemanticTests
{
    [Fact]
    public void Random_NextFloat_InRange()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static bool Test()
                {
                    var f = TinySystem.Random.Shared.NextFloat();
                    return f >= 0.0f && f < 1.0f;
                }
            }
            """, "tostring(T.test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void Random_Next_InRange()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static bool Test()
                {
                    var n = TinySystem.Random.Shared.Next(10);
                    return n >= 0 && n < 10;
                }
            }
            """, "tostring(T.test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void Random_Range_InRange()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static bool Test()
                {
                    var n = TinySystem.Random.Shared.Range(5, 10);
                    return n >= 5 && n <= 10;
                }
            }
            """, "tostring(T.test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void Random_Seed_MakesSequenceDeterministic()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    TinySystem.Random.Seed(42);
                    var a = TinySystem.Random.Shared.Next(1000) + "," + TinySystem.Random.Shared.Range(1, 6)
                        + "," + TinySystem.Random.Shared.NextFloat();
                    TinySystem.Random.Seed(42);
                    var b = TinySystem.Random.Shared.Next(1000) + "," + TinySystem.Random.Shared.Range(1, 6)
                        + "," + TinySystem.Random.Shared.NextFloat();
                    return (a == b) + ":" + a;
                }
            }
            """, "T.test()");
        // seed 42 の列は Lua 5.5 の math.randomseed(42) そのもの (C backend も同じ)
        Assert.StartsWith("true:", result);
    }

    // instance (new Random(seed)) は Shared (Random.Seed) と同じ列。instance
    // 同士も同 seed で一致し、seed 未指定は instance ごとに異なる
    [Fact]
    public void Random_Instance_MatchesSharedStream()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Seq(TinySystem.Random r)
                {
                    var s = "";
                    for (int i = 0; i < 5; i++) s += r.Next(100) + ",";
                    s += r.Range(-3, 3) + "," + r.NextFloat() + "," + r.Next(10, 20) + "," + r.Next();
                    return s;
                }
                public static string Test()
                {
                    TinySystem.Random.Seed(42);
                    var shared = Seq(TinySystem.Random.Shared);
                    var inst = Seq(new TinySystem.Random(42));
                    var again = Seq(new TinySystem.Random(42));
                    var other = Seq(new TinySystem.Random(43));
                    var auto1 = new TinySystem.Random().Next(1000000);
                    var auto2 = new TinySystem.Random().Next(1000000);
                    return (shared == inst) + ":" + (inst == again) + ":" + (inst != other)
                        + ":" + (auto1 != auto2) + ":" + (TinySystem.Random.Shared == TinySystem.Random.Shared);
                }
            }
            """, "T.test()");
        Assert.Equal("true:true:true:true:true", result);
    }
}
