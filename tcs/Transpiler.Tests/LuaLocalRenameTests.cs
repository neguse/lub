namespace TinyCs.Tests;

// Lua 予約語 (local / end / nil ...) と同名のローカル束縛は、宣言と全参照
// (closure 内含む) を安全な名前 (`local_`、衝突時は `_` を足す) に写して emit する。
// member は LuaNaming.Member が既に `end_` に写す。self / __tcs_ prefix は予約の
// ままで、ここでは扱わない (ReservedIdentifier として拒否される)。
public class LuaLocalRenameTests
{
    [Fact]
    public void LocalNamedLocal_RunsAndEmitsSafeName()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Runner
            {
                public static int Run()
                {
                    var local = 1;
                    return local;
                }
            }
            """], checkNaming: false);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("LuaKeywordIdentifier"));
        Assert.Contains("local local_ = 1", result.Lua);
        Assert.Contains("return local_", result.Lua);
        Assert.Equal("1", TestHelper.TranspileAndRun("""
            var r = Runner.Run();

            public class Runner
            {
                public static int Run()
                {
                    var local = 1;
                    return local;
                }
            }
            """, "r"));
    }

    [Fact]
    public void ParameterNamedEnd_RunsAcrossDefaultsAndReferences()
    {
        var output = TestHelper.TranspileAndRun("""
            var timer = new Timer();
            var r = timer.Wait(4) + timer.Wait() + Timer.Add(2, 3);

            public class Timer
            {
                public int Wait(int end = 10) => end * 2;
                public static int Add(int then, int @nil) { return then + @nil; }
            }
            """, "r");

        Assert.Equal("33", output);
    }

    [Fact]
    public void ForEachVariableNamedUntil_Runs()
    {
        var output = TestHelper.TranspileAndRun("""
            using System.Collections.Generic;

            var values = new List<int> { 1, 2, 3 };
            var total = 0;
            foreach (var until in values) total += until;
            foreach (var repeat in new Dictionary<string, int> { ["a"] = 4 })
                total += repeat.Value;
            """, "total");

        Assert.Equal("10", output);
    }

    [Fact]
    public void ClosureCapturingKeywordLocal_RunsConsistently()
    {
        var output = TestHelper.TranspileAndRun("""
            using System;

            var local = 10;
            Func<int, int> add = end => local + end;
            Action bump = () => { local += 1; };
            bump();
            var r = add(5);
            """, "r");

        Assert.Equal("16", output);
    }

    [Fact]
    public void KeywordLocalDoesNotCollideWithUserUnderscoreName()
    {
        var output = TestHelper.TranspileAndRun("""
            var local_ = 100;
            var local = 1;
            var local__ = 1000;
            var r = local * 1 + local_ * 10 + local__ * 100;
            """, "r");

        Assert.Equal("101001", output);
    }

    [Fact]
    public void KeywordLocalDoesNotCaptureSameNamedMemberReference()
    {
        var output = TestHelper.TranspileAndRun("""
            var r = new Box().Get();

            public class Box
            {
                public int end_ = 7;

                public int Get()
                {
                    var end = 1;
                    return end + end_;
                }
            }
            """, "r");

        Assert.Equal("8", output);
    }

    [Fact]
    public void PatternDesignationNamedNil_Runs()
    {
        var output = TestHelper.TranspileAndRun("""
            object value = 5;
            var r = 0;
            if (value is int nil) r += nil;
            r += value switch { int function when function > 0 => function, _ => 0 };
            """, "r");

        Assert.Equal("10", output);
    }

    [Fact]
    public void KeywordLocalInExpressionBodiedAndLegacyBodies_Runs()
    {
        var output = TestHelper.TranspileAndRun("""
            var r = new Walker().Sum(3);

            public class Walker
            {
                public int Sum(int until)
                {
                    var total = 0;
                    for (var repeat = 0; repeat < until; repeat++)
                        total += repeat;
                    return total;
                }
            }
            """, "r");

        Assert.Equal("3", output);
    }

    // incremental session の fast path (body edit → EmitSingleMethod の splice)
    // でも写した tree の method を emit し、full emit と byte 一致する
    [Fact]
    public void KeywordLocal_SurvivesIncrementalSpliceEmit()
    {
        const string fileA = """
            public class Counter
            {
                public int Value;
                public void Add(int end) { var local = end; Value = Value + local; }
                public static int Magic() { return 10; }
            }
            """;
        const string fileB = """
            public class Game
            {
                public static int Play()
                {
                    var c = new Counter();
                    c.Add(Counter.Magic());
                    return c.Value;
                }
            }
            """;
        var session = new IncrementalCompilationSession(checkNaming: false);
        session.OpenProject([("game/Counter.cs", fileA), ("game/Game.cs", fileB)]);
        var edited = fileA.Replace("Value = Value + local;",
            "var until = 1; Value = Value + local + until;");
        var result = session.Update("game/Counter.cs", edited);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.True(result.FastPath);
        var spliced = session.Artifacts.Single(a => a.ModuleId == "game/Counter.cs").Lua;

        var fresh = new IncrementalCompilationSession(checkNaming: false);
        fresh.OpenProject([("game/Counter.cs", edited), ("game/Game.cs", fileB)]);
        var full = fresh.Artifacts.Single(a => a.ModuleId == "game/Counter.cs").Lua;
        Assert.Equal(full, spliced);
        Assert.Contains("local until_ = 1", spliced);
        var lua = string.Join("", session.Artifacts.Select(a => a.Lua));
        Assert.Equal("11", TestHelper.RunLua($"{lua}\nprint(Game.play())").Trim());
    }

    [Fact]
    public void ReservedIdentifiers_StayRejected()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Holder
            {
                public int Get(int self)
                {
                    var __tcs_value = 3;
                    return __tcs_value + self;
                }
            }
            """], checkNaming: false);

        Assert.True(result.Success);
        Assert.Contains(result.Warnings, w => w.Contains("ReservedIdentifier(self)"));
        Assert.Contains(result.Warnings,
            w => w.Contains("ReservedIdentifier(__tcs_value)"));
    }
}
