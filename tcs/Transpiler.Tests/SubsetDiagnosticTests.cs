namespace TinyCs.Tests;

// spec conformance 由来のサブセット診断 (tuple / args / attribute /
// interface member / delegate / decimal / Console allowlist)
public class SubsetDiagnosticTests
{
    private static void AssertUnsupportedError(TranspileResult result, string kind)
    {
        Assert.False(result.Success);
        Assert.Contains(result.Errors,
            e => e.Contains("unsupported") && e.Contains(kind));
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

    // 多次元配列は Lua / C のどちらにも写していない (以前は診断なしで通り、Lua で範囲外の error になった)
    [Fact]
    public void MultiDimensionalArray_IsUnsupported()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            int[,] b = new int[5, 2];
            b[1, 1] = 3;
            System.Console.WriteLine(b[1, 1]);
            """]);

        AssertUnsupportedError(result, "MultiDimensionalArray");
    }

    // 数値の static method は Parse / TryParse だけ写す (float.IsNaN は以前は通り、Lua で nil を呼んだ)
    [Theory]
    [InlineData("float.IsNaN(x)", "float.IsNaN")]
    [InlineData("float.IsInfinity(x)", "float.IsInfinity")]
    [InlineData("int.Max(1, 2) > 0", "int.Max")]
    [InlineData("float.Abs(x) > 0f", "float.Abs")]
    public void NumericStaticMethod_IsUnsupportedApi(string expr, string api)
    {
        var result = Transpiler.TranspileWithDiagnostics([$$"""
            public static class P
            {
                public static bool F(float x) => {{expr}};
            }
            """]);

        Assert.Contains(result.Errors, e => e.Contains("TCS1002") && e.Contains(api));
    }

    [Fact]
    public void NumericParseAndTryParse_AreSupported()
    {
        var result = TestHelper.CompileAndRunVm("""
            public static class P
            {
                public static string F()
                {
                    float.TryParse("2.5", out var f);
                    int.TryParse("x", out var bad);
                    return int.Parse("41") + 1 + ":" + float.Parse("0.5") + ":" + f + ":" + bad;
                }
            }
            """, "P.F()");

        Assert.Equal("42:0.5:2.5:0", result);
    }

    [Fact]
    public void JaggedArray_IsNotMultiDimensional()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            int[][] b = new int[2][];
            b[0] = new int[3];
            System.Console.WriteLine(b[0].Length);
            """]);

        Assert.DoesNotContain(result.Errors.Concat(result.Warnings),
            e => e.Contains("MultiDimensionalArray"));
    }

    [Fact]
    public void TopLevelArgsReference_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            System.Console.WriteLine(args.Length);
            """]);

        AssertUnsupportedError(result, "TopLevelArgs");
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

        AssertUnsupportedError(result, "ConditionalAttribute");
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

        AssertUnsupportedError(result, "InterfaceDefaultMember");
        AssertUnsupportedError(result, "ExplicitInterfaceImplementation");
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
    public void ConsoleMembersOutsideWriteLineAndWrite_ReportErrors()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static void Test()
                {
                    var reader = System.Console.In;
                    System.Console.ReadLine();
                    System.Console.Write("{0}", 1);
                    System.Console.Write("x");
                }
            }
            """]);

        Assert.Contains(result.Errors,
            e => e.Contains("TCS1002") && e.Contains("Console.In"));
        Assert.Contains(result.Errors,
            e => e.Contains("TCS1002") && e.Contains("Console.ReadLine"));
        Assert.Single(result.Errors,
            e => e.Contains("TCS1002") && e.Contains("Console.Write"));
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

        AssertUnsupportedError(result, "ParamsParameter");
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

    // 名前付き引数は位置渡しに落ちて別の意味で動くので error (Lua を返さない)。
    // 同じ build の他の未対応も error で並ぶ (#19)
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
        var named = result.Errors.Where(e => e.Contains(
            "error TCS1001: unsupported syntax: NamedArgument")).ToList();
        Assert.Equal(2, named.Count);
        Assert.StartsWith("(8,11)", named[0]);
        Assert.StartsWith("(8,17)", named[1]);
        Assert.Contains(result.Errors,
            e => e.Contains("error TCS1001") && e.Contains("LocalFunctionStatement"));
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

        AssertUnsupportedError(result, "StaticConstructor");
    }

    // CompareOrdinal の値は符号だけが .NET の契約 (値は 2 文字ずつの比較の位置で
    // 変わる)。0 との比較以外で値を使うと Lua / C の -1 / 0 / 1 と食い違う
    [Fact]
    public void CompareOrdinalValue_OutsideZeroComparison_IsUnsupported()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public static class T
            {
                public static int Diff(string a, string b) => string.CompareOrdinal(a, b);
                public static bool Less(string a, string b) => string.CompareOrdinal(a, b) < 0;
                public static bool One(string a, string b) => string.CompareOrdinal(a, b) == 1;
            }
            """]);

        var errors = result.Errors.Where(e => e.Contains("CompareOrdinalValue")).ToList();
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("(3,"));
        Assert.Contains(errors, e => e.Contains("(5,"));
    }

    [Fact]
    public void CompareOrdinalWithRange_IsUnsupportedApi()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public static class T
            {
                public static bool Less(string a, string b) =>
                    string.CompareOrdinal(a, 0, b, 0, 1) < 0;
            }
            """]);

        Assert.Contains(result.Errors, e => e.Contains("TCS1002")
            && e.Contains("string.CompareOrdinal"));
    }

    // checked で対応するのは i32 の + - * と単項 - (と、checked でも意味の変わらない
    // 除算と float → int の変換)。それ以外の checked な演算は overflow を検出しない
    [Fact]
    public void CheckedOperationOtherThanIntArithmetic_IsUnsupported()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public static class T
            {
                public static int Inc(int a) => checked(a++);
                public static uint Narrow(int a) => checked((uint)a);
                public static int Ok(int a, float f) => checked(a * 2 - (int)f / 3);
            }
            """]);

        var errors = result.Errors.Where(e => e.Contains("CheckedOperation")).ToList();
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("(3,"));
        Assert.Contains(errors, e => e.Contains("(4,"));
        Assert.DoesNotContain(result.Errors, e => e.Contains("(5,"));
    }

    [Fact]
    public void CheckedStatement_IsUnsupported()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public static class T
            {
                public static int F(int a) { checked { return a + 1; } }
            }
            """]);

        AssertUnsupportedError(result, "CheckedStatement");
    }

    // C# の static field は閉じた型ごとに別だが、Lua は型を消すので 1 つを共有してしまう
    [Fact]
    public void StaticFieldOfGenericType_IsUnsupported()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Counter<T>
            {
                public static int Made;
                public static int Total { get; set; }
                public const int Limit = 3;
                public static int Twice(int x) => x * 2;
                public static int Custom { get { return 1; } }
            }
            """]);

        var errors = result.Errors.Where(e => e.Contains("GenericStaticField")).ToList();
        Assert.Equal(2, errors.Count);
        Assert.Contains(errors, e => e.Contains("(3,"));
        Assert.Contains(errors, e => e.Contains("(4,"));
    }

    [Fact]
    public void StaticMethodOverloads_AreSupported()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static void F() { }
                public static void F(object a, object b) { }

                public static void Test() => F();
            }
            """]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
    }

    [Fact]
    public void OverloadMixingStaticAndInstance_IsSupported()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static void F() { }
                public void F(int a) { }
            }
            """]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
    }

    // interface の method を実装する method は interface の method の名前になる。別の interface
    // から同じ名前の method を 2 つ実装すると Lua / C で同じ key になる
    [Fact]
    public void ImplementingSameNameFromTwoInterfaces_IsMethodOverload()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public interface IA { void F(int a); }
            public interface IB { void F(string s); }
            public class T : IA, IB
            {
                public void F(int a) { }
                public void F(string s) { }
            }
            """]);

        AssertUnsupportedError(result, "MethodOverload");
    }

    // overload の名前 (`F__int`) と同じ綴りの member は Lua / C で同じ key になる
    [Fact]
    public void OverloadNameCollidingWithMember_IsMethodOverload()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static void F() { }
                public static void F(int a) { }
                public static void F__int() { }
            }
            """]);

        AssertUnsupportedError(result, "MethodOverload");
    }

    // host (`--ref`) の member は名前に型を付けない (snake_case) ので、overload は同じ key になる。
    // host の宣言の tree は診断しないので呼び出し側で止める
    [Fact]
    public void CallingHostOverload_IsMethodOverload()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public static class T
            {
                public static void Test() { Host.Log.Info("a"); }
            }
            """], referenceSources: ["""
            namespace Host
            {
                public static class Log
                {
                    public static void Info(string s) { }
                    public static void Info(string s, int n) { }
                }
            }
            """]);

        AssertUnsupportedError(result, "MethodOverload");
    }

    // 拡張 method の形の呼び出し、基底の型の同名の method も同じ key になる。private の overload は呼べない
    [Theory]
    [InlineData("o.Poke(1);", true)]
    [InlineData("new Host.Derived().Dup(\"s\");", true)]
    [InlineData("Host.Log.Hidden(\"s\");", false)]
    public void CallingHostOverload_ReducedExtensionBaseAndPrivate(string statement, bool flagged)
    {
        var result = Transpiler.TranspileWithDiagnostics([$$"""
            using Host;
            public static class T
            {
                public static void Test(Host.Obj o) { {{statement}} }
            }
            """], referenceSources: ["""
            namespace Host
            {
                public class Obj { }
                public static class Ext
                {
                    public static void Poke(this Obj o) { }
                    public static void Poke(this Obj o, int n) { }
                }
                public class Base { public void Dup(int n) { } }
                public class Derived : Base { public void Dup(string s) { } }
                public static class Log
                {
                    public static void Hidden(string s) { }
                    private static void Hidden(string s, int n) { }
                }
            }
            """]);

        if (flagged)
            AssertUnsupportedError(result, "MethodOverload");
        else
            Assert.True(result.Success, string.Join("\n", result.Errors));
    }

    // 一覧に無い BCL の型は、書いた地点でも member の地点でも止まる
    [Theory]
    [InlineData("System.Text.StringBuilder? F() => null;", "System.Text.StringBuilder")]
    [InlineData("void F(System.Collections.Generic.LinkedList<int> l) { }", "System.Collections.Generic.LinkedList<T>")]
    [InlineData("int F() => System.Convert.ToInt32(\"1\") + X;", "System.Convert.ToInt32")]
    [InlineData("object? F(object o) => o as System.Exception;", "System.Exception")]
    [InlineData("int F(System.Collections.Generic.IReadOnlyList<int> l) => l.Count;", "System.Collections.Generic.IReadOnlyCollection<T>.Count")]
    public void UnlistedBclType_IsUnsupportedApi(string member, string api)
    {
        var result = Transpiler.TranspileWithDiagnostics([$$"""
            public class T
            {
                static int X;
                {{member}}
            }
            """]);

        Assert.Contains(result.Errors, e => e.Contains($"TCS1002: unsupported API: {api}"));
    }

    // 実行時の表現を持たない型と属性は止めない
    [Fact]
    public void ListedTypesAndAttributes_AreNotUnsupportedApi()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System;
            using System.Collections.Generic;
            [AttributeUsage(AttributeTargets.Class)]
            public class MarkAttribute : Attribute { }
            [Flags]
            public enum E { A = 1, B = 2 }
            [Mark]
            public class T
            {
                public int Sum(IEnumerable<int> xs)
                {
                    var s = 0;
                    foreach (var x in xs) s += x;
                    return s;
                }
                public Func<int, int> F(Action<int> a, Dictionary<int, int>.KeyCollection k)
                {
                    foreach (KeyValuePair<int, int> p in new Dictionary<int, int>()) a(p.Key);
                    return x => k.Count + int.Parse("1") + int.MaxValue;
                }
                public string G(E e, int? n) => e.ToString() + n.HasValue;
            }
            """]);

        Assert.DoesNotContain(result.Errors, e => e.Contains("TCS1002"));
    }

    // 属性は Lua / C に出ない metadata
    [Fact]
    public void AssemblyAttributes_AreNotUnsupportedApi()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            [assembly: System.Reflection.AssemblyVersion("1.0")]
            public static class T { public static int F() => 1; }
            """]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
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

        AssertUnsupportedError(result, "InterfaceField");
    }

    [Fact]
    public void SystemRandom_ReportsError()
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

        Assert.Contains(result.Errors,
            e => e.Contains("TCS1002") && e.Contains("Random"));
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

        AssertUnsupportedError(result, "DelegateDeclaration");
        AssertUnsupportedError(result, "EventDeclaration");
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

        AssertUnsupportedError(result, "DecimalType");
        AssertUnsupportedError(result, "DecimalLiteral");
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

        AssertUnsupportedError(result, "DoubleType");
        AssertUnsupportedError(result, "DoubleLiteral");
        AssertUnsupportedError(result, "LongType");
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

        Assert.DoesNotContain(result.Errors, e => e.Contains("Decimal"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("Decimal"));
    }

    [Fact]
    public void InterfaceTypeTest_OfClass_NoDiagnostic()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public interface IShape { }
            public class Circle : IShape { }
            public class T
            {
                public static string Test(object x)
                {
                    if (x is IShape) return "shape";
                    if (x as IShape != null) return "as";
                    return x switch
                    {
                        IShape s => "pattern",
                        _ => "other",
                    };
                }
            }
            """]);
        Assert.True(result.Success, string.Join("\n", result.Errors));
    }

    [Fact]
    public void InterfaceTypeTest_OfRefInterface_IsRejected()
    {
        var result = Transpiler.TranspileWithDiagnostics(
            ["""
            public class T
            {
                public static bool Test(object o) => o is IHost;
                public static object As(object o) => o as IHost;
            }
            """],
            referenceSources: ["public interface IHost { }"]);
        Assert.Equal(2, result.Errors.Count(e => e.Contains("InterfaceTypeTest")));
    }

    // --ref の generic でない class / interface への型 test と cast は別の名前 (tcs VM は止めない。object-model §10.6)
    [Fact]
    public void RefTypeTestsAndCasts_HaveRefNames()
    {
        var result = Transpiler.TranspileWithDiagnostics(
            ["""
            public class T
            {
                public static bool Test(object o) => o is Host;
                public static Host Cast(object o) => (Host)o;
                public static bool TestI(object o) => o is IHost;
                public static IHost CastI(object o) => (IHost)o;
            }
            """],
            referenceSources: ["public class Host { } public interface IHost { }"]);
        Assert.Contains(result.Errors, e => e.Contains("RefTypeTest(Host)"));
        Assert.Contains(result.Errors, e => e.Contains("RefCast(Host)"));
        Assert.Contains(result.Errors, e => e.Contains("RefInterfaceTypeTest(IHost)"));
        Assert.Contains(result.Errors, e => e.Contains("RefCast(IHost)"));
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
        AssertUnsupportedError(result, "LoneSurrogateLiteral");
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

    // instance method group / 非リテラル alignment は silent wrong-code
    [Fact]
    public void InstanceMethodGroupAndAlignment_ReportErrors()
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
        AssertUnsupportedError(result, "InstanceMethodGroup");
        AssertUnsupportedError(result, "NonConstantAlignment");
        Assert.Single(result.Errors, e => e.Contains("NonConstantAlignment"));
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
        var result = TestHelper.CompileAndRunVm("""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public class T
            {
                public static int Twice(int v) { return v * 2; }
                public static string Test()
                {
                    var xs = new List<int> { 1, 2 };
                    var ys = xs.Select(Twice);
                    return $"{ys[0]}:{ys[1]}";
                }
            }
            """, "T.Test()");
        Assert.Equal("2:4", result);
    }

    // caller info 属性は C# では呼び出し側でコンパイラが引数を埋めるが、
    // tcs は再現せず既定値が渡るので error (#17)
    [Theory]
    [InlineData("CallerArgumentExpression(\"value\")", "CallerArgumentExpression")]
    [InlineData("CallerMemberName", "CallerMemberName")]
    [InlineData("CallerLineNumber", "CallerLineNumber")]
    [InlineData("CallerFilePath", "CallerFilePath")]
    [InlineData("CallerMemberNameAttribute", "CallerMemberName")]
    [InlineData("System.Runtime.CompilerServices.CallerFilePath", "CallerFilePath")]
    [InlineData("global::System.Runtime.CompilerServices.CallerLineNumberAttribute",
        "CallerLineNumber")]
    public void CallerInfoAttribute_ReportsError(string attribute, string kind)
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

        AssertUnsupportedError(result, $"CallerInfoAttribute({kind})");
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
            """], referenceSources: ["""
            namespace Game
            {
                public class CallerMemberNameAttribute : System.Attribute { }
            }
            """]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("CallerInfoAttribute"));
    }

    // 型パターンの型参照を構文のテキストから作らない
    [Fact]
    public void TypeTestWithoutRuntimeType_IsRejected()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System;
            public class T
            {
                public static bool Test(object o)
                {
                    if (o is Func<int // os.execute([[calc]]))) then end --[[
                        >) return true;
                    if (o is Action a) return a != null;
                    return o switch { Func<int, int> => true, _ => false };
                }
            }
            """]);
        Assert.Equal(3, result.Errors.Count(e => e.Contains("TypeTestWithoutRuntimeType")));
        Assert.DoesNotContain(result.Errors, e => e.Contains("expression:"));
    }

    [Fact]
    public void TypeTest_OfSameMetadataStruct_IsNotRejected()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System.Collections.Generic;
            public class T
            {
                public static bool Test(KeyValuePair<int, int> kv) => kv is KeyValuePair<int, int>;
            }
            """]);
        Assert.DoesNotContain(result.Errors, e => e.Contains("TypeTestWithoutRuntimeType"));
    }

    [Fact]
    public void TypeTest_OfGenericReferenceType_IsRejected()
    {
        var result = Transpiler.TranspileWithDiagnostics(
            ["""
            public class T
            {
                public static bool Test(object o) => o is Host<int>;
            }
            """],
            referenceSources: ["public class Host<V> { }"]);
        AssertUnsupportedError(result, "TypeTestWithoutRuntimeType");
    }

    [Fact]
    public void TypeTest_OfSourceDelegate_IsRejected()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public delegate void D();
            public class T
            {
                public static bool Test(object o) => o is D || o is D d && d != null;
            }
            """]);
        Assert.Equal(2, result.Errors.Count(e => e.Contains("TypeTestWithoutRuntimeType")));
    }

    [Fact]
    public void MalformedFormatSpecifier_IsRejected()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static string A(int x) { return $@"{x:F\"" .. os.execute([[id]])) --}"; }
                public static string B(int x) { return $"{x:D\" .. os.execute([[id]])) --}"; }
                public static string C(int x) { return $"{x:R\" .. os.execute([[id]])) --}"; }
                public static string D(int x, float f) { return $"{x:F2}{x:X}{x:D03}{f:R}"; }
            }
            """]);
        Assert.Equal(3, result.Errors.Count(e => e.Contains("MalformedFormatSpecifier")));
    }

    [Fact]
    public void UIntOutsideInt32_IsRejected()
    {
        // uint は i32 と同じ表現。2^31 以上の定数と、結果が 0〜2^31-1 を出うる演算を止める
        var result = Transpiler.TranspileWithDiagnostics(["""
            public enum U : uint { A = 1, Hi = 0x80000000 }
            public static class T
            {
                public static uint A() { return 3000000000u; }
                public static uint B() { return 0xFFFFFFFF; }
                public static int C() { return unchecked((int)0xFFFFFFFF); }
                public static uint D(uint d) { return d * 2u + (d - 1u) + ~d + (d << 1); }
                public static void E(uint d) { d++; --d; d += 1u; d -= 1u; d *= 2u; d <<= 1; }
                public static uint F(uint d) { d &= 3u; d |= 4u; d ^= 1u; d >>= 1; return (d & 3u) | (d >> 1) ^ (d / 2u) % 3u; }
                public static bool G(uint d) { return d < 7u && d != 0xFFu && U.Hi != U.A; }
            }
            """]);
        Assert.Equal(2, result.Errors.Count(e => e.Contains("UIntRange")));
        Assert.Equal(13, result.Errors.Count(e => e.Contains("UIntArithmetic")));
        Assert.Equal(15, result.Errors.Count);
    }

    [Fact]
    public void UnsupportedFormatSpecifier_IsRejected()
    {
        // 独自の書式と、Lua / C が .NET と同じ文字列にできない書式は黙って別の文字列にせず止める
        var result = Transpiler.TranspileWithDiagnostics(["""
            public enum E { A }
            public class T
            {
                public static string A(float f, int i, E e, object o) { return $"{f:0.00}{i:C}{f:P0}{e:X}{o:F1}{i:#,0}"; }
                public static string B(float f, int i, E e, float? n) { return $"{f:F2}{i:X4}{i:D3}{e:G}{e:d}{n:N1}{f:R}{"s":Z}{true:Q}"; }
                public static string C(float f, int i, string s) { return f.ToString("F2") + i.ToString("X") + f.ToString(""); }
                public static string D(float f, int i, string s) { return f.ToString("0.0") + i.ToString(s); }
            }
            """]);
        Assert.Equal(6, result.Errors.Count(e => e.Contains("UnsupportedFormatSpecifier")));
        Assert.Equal(1, result.Errors.Count(e => e.Contains("float.ToString")));
        Assert.Equal(1, result.Errors.Count(e => e.Contains("int.ToString")));
        Assert.Equal(8, result.Errors.Count);
    }

    [Fact]
    public void InterpolationAlignment_UsesConstantValueNotSyntaxText()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static string Test()
                {
                    int x = 5;
                    return $"[{x,/*c*/3}|{x,-/*" .. (os.execute('calc') and ''), 0) --*/3}|{x,0x3}|" + $@"{x,3:D2}";
                }
            }
            """, "T.Test()");
        Assert.Equal("[  5|5  |  5| 05", result);
    }
}
