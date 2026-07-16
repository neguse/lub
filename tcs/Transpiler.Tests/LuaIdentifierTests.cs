namespace TinyCs.Tests;

// C# では合法でも Lua 出力を壊す識別子の扱い:
// - Lua 5.5 予約語 (end, repeat, ...) と同名の宣言は TCS1001 で拒否する
// - verbatim 識別子 (@float 等) は ValueText (@ なし) で emit する
public class LuaIdentifierTests
{
    private static void AssertLuaKeywordWarning(TranspileResult result,
        string keyword)
    {
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Contains(result.Warnings, w =>
            w.Contains($"warning {TinyCsDiagnosticIds.UnsupportedSyntax}:")
            && w.Contains($"LuaKeywordIdentifier({keyword})"));
    }

    [Fact]
    public void MethodNamedLuaKeyword_ReportsUnsupportedSyntax()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Turn
            {
                public void end() { }
            }
            """], checkNaming: false);

        AssertLuaKeywordWarning(result, "end");
    }

    [Fact]
    public void FieldNamedLuaKeyword_ReportsUnsupportedSyntax()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Loop
            {
                public int until;
            }
            """], checkNaming: false);

        AssertLuaKeywordWarning(result, "until");
    }

    [Fact]
    public void LocalNamedLuaKeyword_ReportsUnsupportedSyntax()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Runner
            {
                public int Run()
                {
                    var repeat = 3;
                    return repeat;
                }
            }
            """], checkNaming: false);

        AssertLuaKeywordWarning(result, "repeat");
    }

    [Fact]
    public void ParameterNamedLuaKeyword_ReportsUnsupportedSyntax()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Timer
            {
                public int Wait(int then) => then;
            }
            """], checkNaming: false);

        AssertLuaKeywordWarning(result, "then");
    }

    [Fact]
    public void EnumMemberNamedLuaKeyword_ReportsUnsupportedSyntax()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public enum Token
            {
                nil,
            }
            """], checkNaming: false);

        AssertLuaKeywordWarning(result, "nil");
    }

    [Fact]
    public void ClassNamedLuaKeyword_ReportsUnsupportedSyntax()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class local
            {
                public int X;
            }
            """], checkNaming: false);

        AssertLuaKeywordWarning(result, "local");
    }

    [Fact]
    public void ForEachVariableNamedLuaKeyword_ReportsUnsupportedSyntax()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
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
            """], checkNaming: false);

        AssertLuaKeywordWarning(result, "elseif");
    }

    [Fact]
    public void PatternDesignationNamedLuaKeyword_ReportsUnsupportedSyntax()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Matcher
            {
                public int Read(object value)
                {
                    if (value is int function) return function;
                    return 0;
                }
            }
            """], checkNaming: false);

        AssertLuaKeywordWarning(result, "function");
    }

    [Fact]
    public void VerbatimLuaKeyword_ReportsUnsupportedSyntax()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Turn
            {
                public int Wait(int @end) => @end;
            }
            """], checkNaming: false);

        AssertLuaKeywordWarning(result, "end");
    }

    [Fact]
    public void NonKeywordIdentifiers_HaveNoLuaKeywordWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Game
            {
                public int endFrame;

                public int Render(int index)
                {
                    var localTotal = this.endFrame + index;
                    return localTotal;
                }
            }
            """], checkNaming: false);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("LuaKeywordIdentifier"));
    }

    [Fact]
    public void VerbatimIdentifiers_EmitWithoutAtSign()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Calc
            {
                public int @float = 2;

                public int Add(int @out)
                {
                    var @value = @out + this.@float;
                    return @value;
                }
            }
            """], checkNaming: false);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("LuaKeywordIdentifier"));
        Assert.DoesNotContain("@", result.Lua);
    }

    [Fact]
    public void VerbatimIdentifiers_RunWithCsharpSemantics()
    {
        var output = TestHelper.TranspileAndRun("""
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

    // T151: `self` は Lua method receiver、`__tcs_` prefix は generated temp
    // として emit 側が予約する。宣言すると receiver/temp を壊すため TCS1001。
    private static void AssertReservedIdentifierWarning(TranspileResult result,
        string name)
    {
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Contains(result.Warnings, w =>
            w.Contains($"warning {TinyCsDiagnosticIds.UnsupportedSyntax}:")
            && w.Contains($"ReservedIdentifier({name})"));
    }

    [Fact]
    public void LocalNamedSelf_ReportsUnsupportedSyntax()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Counter
            {
                public int Value = 5;

                public int Get()
                {
                    var self = 1;
                    return Value + self;
                }
            }
            """], checkNaming: false);

        AssertReservedIdentifierWarning(result, "self");
    }

    [Fact]
    public void ParameterNamedSelf_ReportsUnsupportedSyntax()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Mixer
            {
                public int Blend(int self) => self;
            }
            """], checkNaming: false);

        AssertReservedIdentifierWarning(result, "self");
    }

    [Fact]
    public void ClassNamedSelf_ReportsUnsupportedSyntax()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class self
            {
                public int X;
            }
            """], checkNaming: false);

        AssertReservedIdentifierWarning(result, "self");
    }

    [Fact]
    public void LocalWithGeneratedTempPrefix_ReportsUnsupportedSyntax()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Holder
            {
                public int Get()
                {
                    var __tcs_value = 3;
                    return __tcs_value;
                }
            }
            """], checkNaming: false);

        AssertReservedIdentifierWarning(result, "__tcs_value");
    }

    // 旧 temp 名 (__init / __ret) は __tcs_ prefix へ統一し、prefix 予約で
    // 衝突を構造的に防ぐ。__init という名前のユーザー変数は通常どおり動く。
    [Fact]
    public void UserVariableNamedInit_DoesNotCollideWithInitializerTemp()
    {
        var output = TestHelper.TranspileAndRun("""
            var __init = 5;
            var p = new Point { X = __init };
            var check = p.X;

            public class Point
            {
                public int X;
            }
            """, "check");

        Assert.Equal("5", output);
    }

    [Fact]
    public void ContinueLabel_DoesNotCollideWithUserVariable()
    {
        var output = TestHelper.TranspileAndRun("""
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
        var output = TestHelper.TranspileAndRun("""
            Shape s = new @float();
            var isFloat = s is @float f;
            var viaSwitch = s switch
            {
                @float => 1,
                _ => 0,
            };
            var total = (isFloat ? 1 : 0) + viaSwitch;

            public class Shape { }
            public class @float : Shape { }
            """, "total");

        Assert.Equal("2", output);
    }
}
