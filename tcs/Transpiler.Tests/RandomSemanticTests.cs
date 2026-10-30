namespace TinyCs.Tests;

public class RandomSemanticTests
{
    // CPU 時間が 2^31 µs (約 36 分) を越えても os.clock の項は seed に効く。
    // runtime を読み直して instance の連番を揃え、時刻と CPU 時間だけを変える
    [Fact]
    public void Random_AutoSeed_UsesClockBeyondLua32IntegerRange()
    {
        var runtimePath = TestHelper.FindProjectFile("runtime/tinysystem.lua");
        var output = TestHelper.RunLua($"""
            os.time = function() return 0 end
            local function first(clock)
              os.clock = function() return clock end
              return dofile("{runtimePath}").Random.new():Next(2147483647)
            end
            print(first(3000.0) ~= first(4000.0))
            """);
        Assert.Equal("true", output.Trim());
    }

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
            """, "tostring(T.Test())");
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
            """, "tostring(T.Test())");
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
            """, "tostring(T.Test())");
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
            """, "T.Test()");
        // seed 42 の列は Lua 5.5 の math.randomseed(42) そのもの (C backend も同じ)
        Assert.StartsWith("True:", result);
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
            """, "T.Test()");
        Assert.Equal("True:True:True:True:True", result);
    }
}
