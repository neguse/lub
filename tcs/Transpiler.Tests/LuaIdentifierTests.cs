namespace TinyCs.Tests;

// Lua 5.5 の予約語などと重なる識別子の扱い:
// - Lua 5.5 予約語 (end, repeat, ...) と同名の member 宣言は TCS1001 で拒否する
// - ローカル束縛 (local、引数、foreach、pattern の変数) は診断しない (VM での実行は LuaLocalRenameTests)
// - verbatim 識別子 (@float 等) は C# の意味で動く
public class LuaIdentifierTests
{
    private static void AssertLuaKeywordError(CheckResult result,
        string keyword)
    {
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e =>
            e.Contains($"error {TinyCsDiagnosticIds.UnsupportedSyntax}:")
            && e.Contains($"LuaKeywordIdentifier({keyword})"));
    }

    [Fact]
    public void MethodNamedLuaKeyword_ReportsUnsupportedSyntax()
    {
        var result = Checker.Check(["""
            public class Turn
            {
                public void end() { }
            }
            """]);

        AssertLuaKeywordError(result, "end");
    }

    [Fact]
    public void FieldNamedLuaKeyword_ReportsUnsupportedSyntax()
    {
        var result = Checker.Check(["""
            public class Loop
            {
                public int until;
            }
            """]);

        AssertLuaKeywordError(result, "until");
    }

    private static void AssertNoLuaKeywordWarning(CheckResult result)
    {
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("LuaKeywordIdentifier"));
    }

    [Fact]
    public void LocalNamedLuaKeyword_HasNoDiagnostic()
    {
        var result = Checker.Check(["""
            public class Runner
            {
                public int Run()
                {
                    var repeat = 3;
                    return repeat;
                }
            }
            """]);

        AssertNoLuaKeywordWarning(result);
    }

    [Fact]
    public void ParameterNamedLuaKeyword_HasNoDiagnostic()
    {
        var result = Checker.Check(["""
            public class Timer
            {
                public int Wait(int then) => then;
            }
            """]);

        AssertNoLuaKeywordWarning(result);
    }

    [Fact]
    public void RecordPositionalParameterNamedLuaKeyword_ReportsUnsupportedSyntax()
    {
        var result = Checker.Check(["""
            public record Span(int until);
            """]);

        AssertLuaKeywordError(result, "until");
    }

    [Fact]
    public void EnumMemberNamedLuaKeyword_ReportsUnsupportedSyntax()
    {
        var result = Checker.Check(["""
            public enum Token
            {
                nil,
            }
            """]);

        AssertLuaKeywordError(result, "nil");
    }

    [Fact]
    public void ClassNamedLuaKeyword_ReportsUnsupportedSyntax()
    {
        var result = Checker.Check(["""
            public class local
            {
                public int X;
            }
            """]);

        AssertLuaKeywordError(result, "local");
    }

    [Fact]
    public void ForEachVariableNamedLuaKeyword_HasNoDiagnostic()
    {
        var result = Checker.Check(["""
            using System.Collections.Generic;

            public class Walker
            {
                public int Sum(List<int> values)
                {
                    var total = 0;
                    foreach (var elseif in values) total += elseif;
                    return total;
                }
            }
            """]);

        AssertNoLuaKeywordWarning(result);
    }

    [Fact]
    public void PatternDesignationNamedLuaKeyword_HasNoDiagnostic()
    {
        var result = Checker.Check(["""
            public class Matcher
            {
                public int Read(int? value)
                {
                    if (value is int function) return function;
                    return 0;
                }
            }
            """]);

        AssertNoLuaKeywordWarning(result);
    }

    [Fact]
    public void VerbatimLuaKeywordParameter_HasNoDiagnostic()
    {
        var result = Checker.Check(["""
            public class Turn
            {
                public int Wait(int @end) => @end;
            }
            """]);

        AssertNoLuaKeywordWarning(result);
    }

    [Fact]
    public void VerbatimLuaKeywordMethod_ReportsUnsupportedSyntax()
    {
        var result = Checker.Check(["""
            public class Turn
            {
                public int @end() => 1;
            }
            """]);

        AssertLuaKeywordError(result, "end");
    }

    [Fact]
    public void NonKeywordIdentifiers_HaveNoLuaKeywordWarning()
    {
        var result = Checker.Check(["""
            public class Game
            {
                public int endFrame;

                public int Render(int index)
                {
                    var localTotal = this.endFrame + index;
                    return localTotal;
                }
            }
            """]);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("LuaKeywordIdentifier"));
    }

    [Fact]
    public void VerbatimIdentifiers_HaveNoDiagnostic()
    {
        var result = Checker.Check(["""
            public class Calc
            {
                public int @float = 2;

                public int Add(int @out)
                {
                    var @value = @out + this.@float;
                    return @value;
                }
            }
            """]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("LuaKeywordIdentifier"));
    }

    [Fact]
    public void VerbatimIdentifiers_RunWithCsharpSemantics()
    {
        var output = TestHelper.CompileAndRunVmTopLevel("""
            var calc = new Calc();
            var result = calc.Add(5);
            var direct = calc.@float;
            var total = result + direct;

            public class Calc
            {
                public int @float = 2;

                public int Add(int @out)
                {
                    var @value = @out + this.@float;
                    return @value;
                }
            }
            """, "total");

        Assert.Equal("9", output);
    }

    // `self` は Lua method receiver として emit 側が予約する。宣言すると
    // receiver を壊すため TCS1001 (generated temp の `__tcs_` は `__` で始まる
    // 識別子の規則、IdentifierDiagnosticTests)。
    private static void AssertReservedIdentifierError(CheckResult result,
        string name)
    {
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e =>
            e.Contains($"error {TinyCsDiagnosticIds.UnsupportedSyntax}:")
            && e.Contains($"ReservedIdentifier({name})"));
    }

    [Fact]
    public void LocalNamedSelf_ReportsUnsupportedSyntax()
    {
        var result = Checker.Check(["""
            public class Counter
            {
                public int Value = 5;

                public int Get()
                {
                    var self = 1;
                    return Value + self;
                }
            }
            """]);

        AssertReservedIdentifierError(result, "self");
    }

    [Fact]
    public void ParameterNamedSelf_ReportsUnsupportedSyntax()
    {
        var result = Checker.Check(["""
            public class Mixer
            {
                public int Blend(int self) => self;
            }
            """]);

        AssertReservedIdentifierError(result, "self");
    }

    [Fact]
    public void ClassNamedSelf_ReportsUnsupportedSyntax()
    {
        var result = Checker.Check(["""
            public class self
            {
                public int X;
            }
            """]);

        AssertReservedIdentifierError(result, "self");
    }

    [Fact]
    public void ContinueLabel_DoesNotCollideWithUserVariable()
    {
        var output = TestHelper.CompileAndRunVmTopLevel("""
            var _continue_1 = 10;
            var sum = 0;
            for (var i = 0; i < 3; i++)
            {
                if (i == 1) continue;
                sum += i + _continue_1;
            }
            """, "sum");

        Assert.Equal("22", output);
    }

    [Fact]
    public void VerbatimTypeName_EmitsValueTextInPatterns()
    {
        var output = TestHelper.CompileAndRunVmTopLevel("""
            object s = new @float();
            var isFloat = s is @float f;
            var viaSwitch = s switch
            {
                @float => 1,
                _ => 0,
            };
            var total = (isFloat ? 1 : 0) + viaSwitch;

            public class @float { }
            """, "total");

        Assert.Equal("2", output);
    }

    // Lua の runtime が _G に置いた名前 (TinySystem / List / Dict / Math / String など) と同名の型は、
    // Lua の出力では runtime の table を上書きしたので、build を止める error にしている (#21)
    private static void AssertRuntimeGlobalError(CheckResult result,
        string name)
    {
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e =>
            e.Contains($"error {TinyCsDiagnosticIds.UnsupportedSyntax}:")
            && e.Contains($"RuntimeGlobalIdentifier({name})"));
    }

    [Fact]
    public void ClassNamedMath_IsBuildBlockingError()
    {
        var result = Checker.Check(["""
            public class Math
            {
                public static int Twice(int x) => x * 2;
            }
            """]);

        AssertRuntimeGlobalError(result, "Math");
    }

    [Fact]
    public void TypesNamedLikeRuntimeGlobals_InNamespace_AreErrors()
    {
        var result = Checker.Check(["""
            namespace Game
            {
                public class Dict { }
                public struct String { public int X; }
                public enum List { A }
                public class TinySystem { }
            }
            """]);

        AssertRuntimeGlobalError(result, "Dict");
        AssertRuntimeGlobalError(result, "String");
        AssertRuntimeGlobalError(result, "List");
        AssertRuntimeGlobalError(result, "TinySystem");
        Assert.Equal(4, result.Errors.Count);
    }

    [Fact]
    public void NonGlobalTypeNamesAndInterfaces_HaveNoRuntimeGlobalError()
    {
        // interface、member 名 (`self.Math`)、enum member (`E.List`)、`Random` は対象外
        var result = Checker.Check(["""
            namespace Game
            {
                public interface Dict { }
                public record Random(int Seed);
                public enum Kind { List, Math }
                public class MathUtil
                {
                    public int Math = 1;
                    public static int Twice(int x) => x * 2;
                    public int String() => Math;
                }
            }
            """]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("RuntimeGlobalIdentifier"));
    }

    // 予約する型の名前の集合 (Compliance.RuntimeGlobals) を固定する。どの名前の型も error
    [Fact]
    public void RuntimeGlobals_AreTheReservedTypeNames()
    {
        string[] names = ["TinySystem", "List", "Dict", "Math", "String", "TcsQueue", "TcsHashSet"];
        Assert.Equal(names, TcsSelf.Compliance.RuntimeGlobals);
        foreach (var name in names)
            AssertRuntimeGlobalError(Checker.Check([$"public class {name} {{ }}"]), name);
    }
}
