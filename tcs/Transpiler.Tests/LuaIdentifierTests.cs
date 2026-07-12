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
}
