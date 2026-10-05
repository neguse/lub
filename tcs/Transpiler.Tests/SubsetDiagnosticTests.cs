namespace TinyCs.Tests;

// spec conformance 由来のサブセット診断 (tuple / args / attribute /
// interface member / delegate / decimal / Console allowlist)
public class SubsetDiagnosticTests
{
    private static void AssertUnsupportedWarning(TranspileResult result, string kind)
    {
        Assert.True(result.Success);
        Assert.Contains(result.Warnings,
            w => w.Contains("unsupported") && w.Contains(kind));
    }

    [Fact]
    public void TupleTypeAndTupleLiteral_ReportWarnings()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static int Test()
                {
                    (string name, int age) a = ("Bert", 42);
                    return a.age;
                }
            }
            """]);

        AssertUnsupportedWarning(result, "TupleType");
        AssertUnsupportedWarning(result, "TupleExpression");
    }

    [Fact]
    public void DeconstructionAssignmentTarget_IsNotFlaggedAsTuple()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public record Point(int X, int Y);
            public class T
            {
                public static int Test()
                {
                    var p = new Point(1, 2);
                    var (a, b) = p;
                    int x;
                    int y;
                    (x, y) = p;
                    return a + b + x + y;
                }
            }
            """]);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("Tuple"));
    }

    [Fact]
    public void TopLevelArgsReference_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            System.Console.WriteLine(args.Length);
            """]);

        AssertUnsupportedWarning(result, "TopLevelArgs");
    }

    [Fact]
    public void LambdaParameterNamedArgs_IsNotFlagged()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            System.Func<string[], int> f = args => args.Length;
            System.Console.WriteLine(f(new[] { "a" }));
            """]);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("TopLevelArgs"));
    }

    [Fact]
    public void ConditionalAttribute_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                [System.Diagnostics.Conditional("DEBUG")]
                public static void Log() { }

                public static void Test() => Log();
            }
            """]);

        AssertUnsupportedWarning(result, "ConditionalAttribute");
    }

    [Fact]
    public void OtherAttributes_AreNotFlagged()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                [System.Obsolete("old")]
                public static int Test() => 1;
            }
            """]);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("ConditionalAttribute"));
    }

    [Fact]
    public void InterfaceDefaultMemberAndExplicitImplementation_ReportWarnings()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public interface IA
            {
                int P { get { return 10; } }
                void M() { }
            }

            public class C : IA
            {
                void IA.M() { }
            }
            """]);

        AssertUnsupportedWarning(result, "InterfaceDefaultMember");
        AssertUnsupportedWarning(result, "ExplicitInterfaceImplementation");
    }

    [Fact]
    public void PlainInterfaceContract_IsNotFlagged()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public interface IShape
            {
                int Area();
                int Size { get; set; }
            }

            public class Box : IShape
            {
                public int Size { get; set; }
                public int Area() => Size * Size;
            }
            """]);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Warnings, w =>
            w.Contains("InterfaceDefaultMember") ||
            w.Contains("ExplicitInterfaceImplementation"));
    }

    [Fact]
    public void ConsoleMembersOutsideWriteLine_ReportWarnings()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static void Test()
                {
                    var reader = System.Console.In;
                    System.Console.Write("x");
                }
            }
            """]);

        Assert.Contains(result.Warnings,
            w => w.Contains("TCS1002") && w.Contains("Console.In"));
        Assert.Contains(result.Warnings,
            w => w.Contains("TCS1002") && w.Contains("Console.Write"));
    }

    [Fact]
    public void ParamsParameter_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static int F(params int[] xs) => xs.Length;

                public static int Test() => F(1, 2, 3);
            }
            """]);

        AssertUnsupportedWarning(result, "ParamsParameter");
    }

    [Fact]
    public void PlainArrayParameter_IsNotFlagged()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static int F(int[] xs) => xs.Length;

                public static int Test() => F(new[] { 1, 2, 3 });
            }
            """]);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("ParamsParameter"));
    }

    // 名前付き引数は位置渡しに落ちて別の意味で動くため、warning ではなく
    // build を止める error (Lua を返さない)。他の警告は残る (#19)
    [Fact]
    public void NamedArgument_IsBuildBlockingError()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static void F(int x, int y = -1) { }

                public static int Test()
                {
                    static int G() => 1;
                    F(y: 2, x: 1);
                    return G();
                }
            }
            """]);

        Assert.False(result.Success);
        Assert.Equal("", result.Lua);
        // 名前付き引数 1 つにつき 1 件 (y: / x:)
        Assert.Equal(2, result.Errors.Count);
        Assert.All(result.Errors, e => Assert.Contains(
            "error TCS1001: unsupported syntax: NamedArgument", e));
        Assert.StartsWith("(8,11)", result.Errors[0]);
        Assert.StartsWith("(8,17)", result.Errors[1]);
        Assert.Contains(result.Warnings,
            w => w.Contains("warning TCS1001") && w.Contains("LocalFunctionStatement"));
        Assert.True(TinyCsComplianceFacts.IsBuildBlocking("NamedArgument"));
        Assert.False(TinyCsComplianceFacts.IsBuildBlocking("LocalFunctionStatement"));
    }

    [Fact]
    public void StaticConstructor_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                static T() { }

                public static int Test() => 1;
            }
            """]);

        AssertUnsupportedWarning(result, "StaticConstructor");
    }

    [Fact]
    public void MethodOverloads_ReportWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static void F() { }
                public static void F(object a, object b) { }

                public static void Test() => F();
            }
            """]);

        AssertUnsupportedWarning(result, "MethodOverload");
    }

    [Fact]
    public void DistinctMethodNames_AreNotFlaggedAsOverloads()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static void F() { }
                public static void G() { }

                public static void Test() { F(); G(); }
            }
            """]);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("MethodOverload"));
    }

    [Fact]
    public void NewMemberHiding_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class A
            {
                public void F() { }
                public virtual void G() { }
            }

            public class B : A
            {
                public new void F() { }
                public override void G() { }
            }
            """]);

        AssertUnsupportedWarning(result, "NewMemberHiding");
        Assert.DoesNotContain(result.Warnings, w => w.Contains("G"));
    }

    [Fact]
    public void InterfaceConstField_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public interface IX
            {
                const int A = 1;
            }

            public class T
            {
                public static int Test() => IX.A;
            }
            """]);

        AssertUnsupportedWarning(result, "InterfaceField");
    }

    [Fact]
    public void SystemRandom_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static float Test()
                {
                    var r = new System.Random();
                    return (float)r.NextDouble();
                }
            }
            """]);

        Assert.Contains(result.Warnings,
            w => w.Contains("TCS1002") && w.Contains("Random"));
    }

    [Fact]
    public void ConsoleWriteLine_IsNotFlagged()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static void Test()
                {
                    System.Console.WriteLine("x");
                    System.Console.WriteLine(42);
                }
            }
            """]);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("TCS1002"));
    }

    [Fact]
    public void DelegateAndEventDeclarations_ReportWarnings()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public delegate void Handler(int x);

            public class T
            {
                public event Handler? Changed;

                public static int Test() => 1;
            }
            """]);

        AssertUnsupportedWarning(result, "DelegateDeclaration");
        AssertUnsupportedWarning(result, "EventDeclaration");
    }

    [Fact]
    public void ActionAndFuncFields_AreNotFlaggedAsDelegates()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public System.Action? OnDone;

                public static int Test() => 1;
            }
            """]);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("DelegateDeclaration") ||
                 w.Contains("EventDeclaration"));
    }

    [Fact]
    public void DecimalTypeAndLiteral_ReportWarnings()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static decimal Test()
                {
                    var d = 2.900m;
                    return d;
                }
            }
            """]);

        AssertUnsupportedWarning(result, "DecimalType");
        AssertUnsupportedWarning(result, "DecimalLiteral");
    }

    [Fact]
    public void DoubleAndLongTypesAndDoubleLiteral_ReportWarnings()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public double Field;
                public long Signed;
                public ulong Unsigned;

                public static double Test(long value, ulong other)
                {
                    double result = 2.5;
                    return result + value + other;
                }
            }
            """]);

        AssertUnsupportedWarning(result, "DoubleType");
        AssertUnsupportedWarning(result, "DoubleLiteral");
        AssertUnsupportedWarning(result, "LongType");
    }

    [Fact]
    public void FloatLiteral_DoesNotReportDoubleLiteral()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static float Test()
                {
                    float value = 1.5f;
                    return value;
                }
            }
            """]);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("DoubleType") || w.Contains("DoubleLiteral"));
    }

    [Fact]
    public void FloatAndDoubleLiterals_AreNotFlaggedAsDecimal()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static double Test()
                {
                    float f = 1.5f;
                    double d = 2.5;
                    return f + d;
                }
            }
            """]);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("Decimal"));
    }

    // interface は実行時表現を持たないため、interface を対象とする
    // type test は常に偽になる (il-spec §2)。診断で拒否する。
    [Fact]
    public void InterfaceTypeTest_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public interface IShape { }
            public class Circle : IShape { }
            public class T
            {
                public static string Test(object x)
                {
                    if (x is IShape) return "shape";
                    return x switch
                    {
                        IShape s => "pattern",
                        _ => "other",
                    };
                }
            }
            """]);
        AssertUnsupportedWarning(result, "InterfaceTypeTest");
    }

    [Fact]
    public void ClassTypeTest_NoWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Animal { }
            public class T
            {
                public static bool Test(object x) { return x is Animal; }
            }
            """]);
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("InterfaceTypeTest"));
    }

    // 孤立 surrogate は UTF-8 octet 列への写像を持たない (il-spec §11)
    [Fact]
    public void LoneSurrogateLiteral_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static string Test() { return "bad\uD800end"; }
            }
            """]);
        AssertUnsupportedWarning(result, "LoneSurrogateLiteral");
    }

    [Fact]
    public void PairedSurrogateLiteral_NoWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static string Test() { return "ok\U0001F600end"; }
            }
            """]);
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("LoneSurrogateLiteral"));
    }

    // nested class は emit されず参照時に実行時 nil になる
    [Fact]
    public void NestedClass_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Outer
            {
                public class Inner { public int V; }
                public static object Make() { return new Inner(); }
            }
            """]);
        AssertUnsupportedWarning(result, "NestedTypeDeclaration");
    }

    // instance method group / 非リテラル alignment は silent wrong-code
    [Fact]
    public void InstanceMethodGroupAndAlignment_ReportWarnings()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System;
            public class T
            {
                public int V;
                public int Get() { return V; }
                public object Grab()
                {
                    Func<int> f = Get;
                    return f;
                }
                public static string Fmt(int x) { return $"{x,5}|{x:D2}"; }
                public static string Bad(int x) { return $"{x,2 + 3}"; }
            }
            """]);
        Assert.True(result.Success);
        AssertUnsupportedWarning(result, "InstanceMethodGroup");
        AssertUnsupportedWarning(result, "NonConstantAlignment");
        Assert.Single(result.Warnings, w => w.Contains("NonConstantAlignment"));
    }

    // `x?.M()` の `.M` は呼び出し位置 (MemberBinding) — method group 扱いしない
    [Fact]
    public void ConditionalInvocation_IsNotMethodGroup()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public struct V { public int X; public int Sum() { return X; } }
            public class T
            {
                public int Get() { return 1; }
                public static int Run(T t, V? v)
                {
                    int? a = t?.Get();
                    int? b = v?.Sum();
                    return (a ?? 0) + (b ?? 0);
                }
            }
            """]);
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("InstanceMethodGroup"));
    }

    [Fact]
    public void StaticMethodGroup_NoWarning_AndWorks()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static int Twice(int v) { return v * 2; }
                public static string Test()
                {
                    var xs = new List<int> { 1, 2 };
                    var ys = xs.Select(Twice).ToList();
                    return $"{ys[0]}:{ys[1]}";
                }
            }
            """, "T.Test()");
        Assert.Equal("2:4", result);
    }

    // caller info 属性は C# では呼び出し側でコンパイラが引数を埋めるが、
    // tcs は再現せず既定値が渡る。少なくとも警告する (#17)。build は止めない
    [Theory]
    [InlineData("CallerArgumentExpression(\"value\")", "CallerArgumentExpression")]
    [InlineData("CallerMemberName", "CallerMemberName")]
    [InlineData("CallerLineNumber", "CallerLineNumber")]
    [InlineData("CallerFilePath", "CallerFilePath")]
    [InlineData("CallerMemberNameAttribute", "CallerMemberName")]
    [InlineData("System.Runtime.CompilerServices.CallerFilePath", "CallerFilePath")]
    [InlineData("global::System.Runtime.CompilerServices.CallerLineNumberAttribute",
        "CallerLineNumber")]
    public void CallerInfoAttribute_ReportsWarning(string attribute, string kind)
    {
        var type = kind == "CallerLineNumber" ? "int" : "string";
        var dflt = kind == "CallerLineNumber" ? "0" : "\"\"";
        var result = Transpiler.TranspileWithDiagnostics([$$"""
            using System.Runtime.CompilerServices;

            public static class CallerInfoRepro
            {
                public static {{type}} Where(int value,
                    [{{attribute}}] {{type}} text = {{dflt}})
                {
                    return text;
                }

                public static {{type}} Test() => Where(1 + 2);
            }
            """]);

        AssertUnsupportedWarning(result, $"CallerInfoAttribute({kind})");
        Assert.NotEmpty(result.Lua);
        Assert.False(TinyCsComplianceFacts.IsBuildBlocking(
            $"CallerInfoAttribute({kind})"));
    }

    [Fact]
    public void OtherAttributes_AreNotFlaggedAsCallerInfo()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public static class Plain
            {
                [System.Obsolete("old")]
                public static int Where(int value) => value;

                [Game.CallerMemberName]
                public static int Test() => Where(1);
            }
            namespace Game
            {
                public class CallerMemberNameAttribute : System.Attribute { }
            }
            """]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("CallerInfoAttribute"));
    }
}
