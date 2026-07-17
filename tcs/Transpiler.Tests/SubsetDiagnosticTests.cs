namespace TinyCs.Tests;

// T188 以降の spec conformance 由来サブセット診断 (tuple / args / attribute /
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
                public static double Test()
                {
                    var r = new System.Random();
                    return r.NextDouble();
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
}
