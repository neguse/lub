namespace TinyCs.Tests;

// 型の名前や IL が素の名前で参照する名前と同名のローカル束縛は、宣言と全参照
// (closure 内含む) を IL では別の名前 (`V_`、衝突時は `_` を足す) に写す。診断はせず、VM で C# と同じに動く。
// 他の言語の予約語 (local / end / nil ...) と同名のローカル束縛は、そのままの名前で動く。
// self は予約のままで、ここでは扱わない (ReservedIdentifier として拒否される。`__` で始まる名前は
// DoubleUnderscoreIdentifier の error)。
public class LocalRenameTests
{
    [Fact]
    public void LocalNamedLocal_RunsWithoutDiagnostic()
    {
        var result = Checker.Check(["""
            public class Runner
            {
                public static int Run()
                {
                    var local = 1;
                    return local;
                }
            }
            """]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Empty(result.Warnings);
        Assert.Equal("1", TestHelper.CompileAndRunVmTopLevel("""
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
        var output = TestHelper.CompileAndRunVmTopLevel("""
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
        var output = TestHelper.CompileAndRunVmTopLevel("""
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
        var output = TestHelper.CompileAndRunVmTopLevel("""
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
        var output = TestHelper.CompileAndRunVmTopLevel("""
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
        var output = TestHelper.CompileAndRunVmTopLevel("""
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
        var output = TestHelper.CompileAndRunVmTopLevel("""
            int? value = 5;
            var r = 0;
            if (value is int nil) r += nil;
            r += value switch { int function when function > 0 => function, _ => 0 };
            """, "r");

        Assert.Equal("10", output);
    }

    [Fact]
    public void KeywordLocalInExpressionBodiedAndBlockBodies_Runs()
    {
        var output = TestHelper.CompileAndRunVmTopLevel("""
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

    [Fact]
    public void ReservedIdentifiers_StayRejected()
    {
        var result = Checker.Check(["""
            public class Holder
            {
                public int Get(int self)
                {
                    return self;
                }
            }
            """]);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("ReservedIdentifier(self)"));
    }

    // 型名と同名のローカル束縛は、IL の型参照 (operator の static 呼び
    // 出し `V.__add(a, b)` / `V.new(...)` / 型内の static member 参照) を
    // 隠すので、別の名前に写す
    [Fact]
    public void ParameterNamedLikeOperatorOwnerType_Runs()
    {
        var output = TestHelper.CompileAndRunVm("""
            public class V {
                public int X;
                public V(int x) { X = x; }
                public static V operator +(V a, V b) {
                    return new V(a.X + b.X);
                }
                public static int Sum(V a, int V) {
                    return (a + a).X;
                }
            }
            public class T {
                public static int Test() {
                    return V.Sum(new V(3), 0);
                }
            }
            """, "T.Test()");

        Assert.Equal("6", output);
    }

    [Fact]
    public void LocalNamedLikeType_ConstructorAndStaticMemberStillResolve()
    {
        var output = TestHelper.CompileAndRunVm("""
            public class W {
                public static int Base = 10;
                public int X;
                public W(int x) { X = x; }
                public static int Make(int W) {
                    var w = new W(W);
                    return w.X + Base;
                }
            }
            public class T {
                public static int Test() {
                    return W.Make(5) + Twice(5);
                }
                static int Twice(int W) { return new W(W).X * 2; }
            }
            """, "T.Test()");

        Assert.Equal("25", output);
    }

    private const string QualifiedOperatorSource = """
        namespace A
        {
            public class V
            {
                public int X;
                public V(int x) { X = x; }
                public static V operator +(V a, V b) => new V(a.X + b.X);
                public static int Sum(V a, int A_V) => (a + a).X + A_V;
                public static int SumParameters(V a, int A_V, int A_V_) =>
                    (a + a).X + A_V * 10 + A_V_;
            }
        }
        namespace B { public class V { } }
        public class Runner
        {
            public static int One() => A.V.Sum(new A.V(3), 7);
            public static int Parameters() =>
                A.V.SumParameters(new A.V(3), 2, 7);
        }
        """;

    // #61 の namespace 修飾名も #62 の operator 直呼びを遮蔽する。
    [Fact]
    public void ParameterNamedLikeQualifiedOperatorOwner_Runs()
    {
        Assert.Equal("13", TestHelper.CompileAndRunVm(
            QualifiedOperatorSource, "Runner.One()"));
    }

    // A_V を A_V_ に写すと同じ body の parameter A_V_ と重なるため、更に _ を足す
    [Fact]
    public void QualifiedAliasParameters_ReceiveDistinctReplacementNames()
    {
        Assert.Equal("33", TestHelper.CompileAndRunVm(
            QualifiedOperatorSource, "Runner.Parameters()"));
    }

    [Fact]
    public void QualifiedOperatorOwnerNames_AreReservedInIlExport()
    {
        var result = IlExport.Export([QualifiedOperatorSource]);
        Assert.Empty(result.Diagnostics);
        var owner = result.Classes.Single(c => c.Name == "A_V");
        var parameters = owner.Methods.Single(m => m.Name == "SumParameters");
        Assert.Equal("A_V__", parameters.Parameters[1]);
        Assert.Equal("A_V_", parameters.Parameters[2]);
    }

    [Fact]
    public void NameofQualifiedAliasParameter_KeepsOriginalSpelling()
    {
        var source = QualifiedOperatorSource.Replace(
            "public static int Sum(V a, int A_V)",
            "public static string Name(int A_V) => nameof(A_V); "
                + "public static int Sum(V a, int A_V)").Replace(
            "public static int One()",
            "public static string Name() => A.V.Name(7); public static int One()");
        Assert.Equal("A_V", TestHelper.CompileAndRunVm(source, "Runner.Name()"));
    }
}
