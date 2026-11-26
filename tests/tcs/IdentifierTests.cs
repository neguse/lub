namespace TinyCs.Tests;

// Handling of identifiers:
// - Names that are not C# reserved words (end, local, nil, repeat, etc.) can be used for members, types, and locals
// - Verbatim identifiers (@float, etc.) behave with their C# meaning
// - self, and types with the same name as runtime globals, are TCS1001
public class IdentifierTests
{
    // tcs VM and C names are built from C# identifiers, so names that match reserved words of other languages get no special treatment
    [Fact]
    public void MembersAndTypesNamedLikeOtherLanguageKeywords_HaveNoDiagnostic()
    {
        var result = Checker.Check(["""
            public record Span(int until);
            public enum Token { nil, then }
            public class local
            {
                public int function;
                public int repeat { get; set; }
                public int end() => function + repeat;
                public int @elseif() => 1;
            }
            """]);

        AssertNoDiagnostic(result);
    }

    [Fact]
    public void MembersAndTypesNamedLikeOtherLanguageKeywords_Run()
    {
        var output = TestHelper.CompileAndRunVmTopLevel("""
            var value = new local();
            value.function = 3;
            value.repeat = 4;
            var span = new Span(5);
            var token = Token.then;
            var total = value.end() + value.elseif() + span.until + (int)token + value.end_;

            public record Span(int until);
            public enum Token { nil, then }
            public class local
            {
                public int end_ = 100;
                public int function;
                public int repeat { get; set; }
                public int end() => function + repeat;
                public int @elseif() => 1;
            }
            """, "total");

        Assert.Equal("114", output);
    }

    private static void AssertNoDiagnostic(CheckResult result)
    {
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void LocalNamedLikeOtherLanguageKeyword_HasNoDiagnostic()
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

        AssertNoDiagnostic(result);
    }

    [Fact]
    public void ParameterNamedLikeOtherLanguageKeyword_HasNoDiagnostic()
    {
        var result = Checker.Check(["""
            public class Timer
            {
                public int Wait(int then) => then;
            }
            """]);

        AssertNoDiagnostic(result);
    }

    [Fact]
    public void ForEachVariableNamedLikeOtherLanguageKeyword_HasNoDiagnostic()
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

        AssertNoDiagnostic(result);
    }

    [Fact]
    public void PatternDesignationNamedLikeOtherLanguageKeyword_HasNoDiagnostic()
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

        AssertNoDiagnostic(result);
    }

    [Fact]
    public void VerbatimParameter_HasNoDiagnostic()
    {
        var result = Checker.Check(["""
            public class Turn
            {
                public int Wait(int @end) => @end;
            }
            """]);

        AssertNoDiagnostic(result);
    }

    [Fact]
    public void IdentifiersContainingKeywords_HaveNoDiagnostic()
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

        AssertNoDiagnostic(result);
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

        AssertNoDiagnostic(result);
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

    // IL uses `self` as the name of a method's receiver. Declaring it
    // breaks the receiver, so it is TCS1001 (the generated temp `__tcs_` falls under the rule for identifiers starting with `__`;
    // see IdentifierDiagnosticTests).
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

    // Types with the same name as runtime names that IL references by bare name (TinySystem / List / Dict / Math / String, etc.) are made a build-stopping error
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
        // Interfaces, member names (`self.Math`), enum members (`E.List`), and `Random` are out of scope
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

    // Pins down the set of reserved type names (Compliance.RuntimeGlobals). A type with any of these names is an error
    [Fact]
    public void RuntimeGlobals_AreTheReservedTypeNames()
    {
        string[] names = ["TinySystem", "List", "Dict", "Math", "String", "TcsQueue", "TcsHashSet"];
        Assert.Equal(names, Tcs.Compliance.RuntimeGlobals);
        foreach (var name in names)
            AssertRuntimeGlobalError(Checker.Check([$"public class {name} {{ }}"]), name);
    }
}
