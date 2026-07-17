namespace TinyCs.Tests;

public class DiagnosticTests
{
    private static void AssertUnsupportedWarning(TranspileResult result, string kind)
    {
        Assert.True(result.Success);
        Assert.Contains(result.Warnings,
            w => w.Contains("unsupported") && w.Contains(kind));
    }

    [Fact]
    public void CompileError_ReportsWithLocation()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static int Test()
                {
                    return UndefinedVariable;
                }
            }
            """]);
        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Contains("error CS", result.Errors[0]);
    }

    [Fact]
    public void ValidCode_NoErrors()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static int Test() { return 42; }
            }
            """]);
        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.NotEmpty(result.Lua);
    }

    [Fact]
    public void MultipleFiles_CompileError()
    {
        var result = Transpiler.TranspileWithDiagnostics([
            "public class A { public int X; }",
            """
            public class B
            {
                public static int Test()
                {
                    var a = new A();
                    return a.Y;
                }
            }
            """
        ]);
        Assert.False(result.Success);
        Assert.Contains("error CS", result.Errors[0]);
    }

    [Theory]
    [InlineData("public class T { public static int Test() { int x = \"oops\"; return x; } }", "CS0029")]
    [InlineData("public class T { public static int Test() { int x = 1.5; return x; } }", "CS0266")]
    [InlineData("public class T { public static bool Test() => true + false; }", "CS0019")]
    [InlineData("public interface IRun { void Run(); } public class T : IRun { }", "CS0535")]
    public void OrdinaryErrors_WithTinyCsExceptionIds_AreRejected(
        string source, string diagnosticId)
    {
        var result = Transpiler.TranspileWithDiagnostics([source]);

        Assert.False(result.Success);
        Assert.Contains(result.Errors,
            error => error.Contains($"error {diagnosticId}:"));
    }

    [Fact]
    public void NestedTypeError_IsNotHiddenByOuterEnumConversion()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System;

            public enum State { Idle }
            public class T
            {
                public static int Test()
                {
                    int value = ((Func<State>)(() =>
                    {
                        int invalid = "oops";
                        return State.Idle;
                    }))();
                    return value;
                }
            }
            """]);

        Assert.False(result.Success);
        Assert.Contains(result.Errors,
            error => error.Contains("error CS0029:"));
    }

    [Fact]
    public void EnumInteger_NonEqualityOperator_IsRejected()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public enum State { Idle }
            public class T
            {
                public static bool Test() => State.Idle < 1;
            }
            """]);

        Assert.False(result.Success);
        Assert.Contains(result.Errors,
            error => error.Contains("error CS0019:"));
    }

    [Theory]
    [InlineData("public enum E { A = 65 } public class T { public static E Test() { E value = 'A'; return value; } }")]
    [InlineData("public enum E { A = 65 } public class T { public static char Test() { char value = E.A; return value; } }")]
    public void EnumCharConversion_IsRejected(string source)
    {
        var result = Transpiler.TranspileWithDiagnostics([source]);

        Assert.False(result.Success);
        Assert.Contains(result.Errors,
            error => error.Contains("error CS0266:"));
    }

    [Theory]
    [InlineData("public interface IValue { int Value { get; } } public class T : IValue { public string Value = \"\"; }")]
    [InlineData("public interface IValue { int Value { get; } } public class T : IValue { private int Value; }")]
    [InlineData("public interface IValue { int Value { get; } } public class T : IValue { public static int Value; }")]
    [InlineData("public interface IValue { int Value { get; set; } } public class T : IValue { public readonly int Value; }")]
    [InlineData("public interface IValue { int Value { get; } void Run(); } public class T : IValue { public int Value; }")]
    [InlineData("public interface IEvents { event System.Action Changed; } public class T : IEvents { public System.Action Changed; }")]
    [InlineData("public interface IValue { int Value { get; } } public class Base { public int Value; } public class T : Base, IValue { }")]
    public void InterfaceFieldFacade_InvalidShape_IsRejected(string source)
    {
        var result = Transpiler.TranspileWithDiagnostics([source]);

        Assert.False(result.Success);
        Assert.Contains(result.Errors,
            error => error.Contains("error CS0535:"));
    }

    [Theory]
    [InlineData("public interface I<T> where T : I<T> { int Value { get; } static abstract T operator +(T left, T right); } public class C : I<C> { public int Value; }")]
    [InlineData("public interface I<T> where T : I<T> { int Value { get; } static abstract implicit operator int(T value); } public class C : I<C> { public int Value; }")]
    public void InterfaceFieldFacade_StaticAbstractMember_IsRejected(
        string source)
    {
        var result = Transpiler.TranspileWithDiagnostics([source]);

        Assert.False(result.Success);
        Assert.Contains(result.Errors,
            error => error.Contains("error CS0535:"));
    }

    [Fact]
    public void UnsupportedStructDeclaration_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public struct Vec2
            {
                public int X;
            }
            """]);

        AssertUnsupportedWarning(result, "StructDeclaration");
    }

    [Fact]
    public void UnsupportedRecordStructDeclaration_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public readonly record struct Vec2(int X, int Y);
            """]);

        AssertUnsupportedWarning(result, "RecordStructDeclaration");
    }

    [Fact]
    public void UnsupportedThrowStatement_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static void Test()
                {
                    throw new System.Exception();
                }
            }
            """]);

        AssertUnsupportedWarning(result, "ThrowStatement");
    }

    [Fact]
    public void UnsupportedTryStatement_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static int Test()
                {
                    try
                    {
                        return 1;
                    }
                    catch
                    {
                        return 2;
                    }
                }
            }
            """]);

        AssertUnsupportedWarning(result, "TryStatement");
    }

    [Fact]
    public void UnsupportedNestedSyntaxes_ReportWarnings()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static int Test()
                {
                    try
                    {
                        throw new System.Exception();
                    }
                    catch
                    {
                        return 2;
                    }
                }
            }
            """]);

        AssertUnsupportedWarning(result, "TryStatement");
        AssertUnsupportedWarning(result, "ThrowStatement");
    }

    [Fact]
    public void UnsupportedUsingStatement_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Disposable : System.IDisposable
            {
                public void Dispose() {}
            }

            public class T
            {
                public static void Test()
                {
                    using (var d = new Disposable())
                    {
                    }
                }
            }
            """]);

        AssertUnsupportedWarning(result, "UsingStatement");
    }

    [Fact]
    public void UnsupportedUsingDeclaration_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Disposable : System.IDisposable
            {
                public void Dispose() {}
            }

            public class T
            {
                public static void Test()
                {
                    using var d = new Disposable();
                }
            }
            """]);

        AssertUnsupportedWarning(result, "UsingDeclaration");
    }

    [Fact]
    public void UnsupportedLocalFunction_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static int Test()
                {
                    int Local() => 1;
                    return Local();
                }
            }
            """]);

        AssertUnsupportedWarning(result, "LocalFunctionStatement");
    }

    [Fact]
    public void UnsupportedPattern_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static bool Test(int[] values)
                {
                    return values is [1, 2];
                }
            }
            """]);

        AssertUnsupportedWarning(result, "ListPattern");
    }

    [Fact]
    public void PartialTypesAndLock_ReportWarningsWithoutWrongCodeEmission()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public partial class PartialClass
            {
                public static int First() => 1;
            }
            public partial class PartialClass
            {
                public static int Second() => 2;
            }

            public partial record PartialRecord;
            public partial interface IPartial { }

            public class Locker
            {
                public static int Test()
                {
                    lock (new object())
                    {
                        return 1;
                    }
                }
            }
            """]);

        var syntaxWarnings = result.Warnings
            .Where(w => w.Contains(TinyCsDiagnosticIds.UnsupportedSyntax))
            .ToArray();

        Assert.True(result.Success);
        Assert.Equal(5, syntaxWarnings.Length);
        Assert.Equal(4, syntaxWarnings.Count(
            warning => warning.Contains("PartialTypeDeclaration")));
        Assert.Single(syntaxWarnings,
            warning => warning.Contains("LockStatement"));
        Assert.DoesNotContain("PartialClass = {}", result.Lua);
        Assert.DoesNotContain("PartialRecord = {}", result.Lua);
        Assert.Equal(4, result.Lua.Split(
            "--[[ unsupported: PartialTypeDeclaration ]]",
            StringSplitOptions.None).Length - 1);
        Assert.Contains("--[[ unsupported: LockStatement ]]", result.Lua);
        Assert.Equal("1", TestHelper.RunLua(
            $"{result.Lua}\nprint(Locker.Test())").Trim());
    }

    [Fact]
    public void NameOfExpressions_ReportWarningsAndEmitConstantMarkers()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class NameDemo
            {
                public static string Simple(int value) => nameof(value);
                public static string MemberName() => nameof(System.Math.E);
                public static string TypeName() => nameof(System.DateTime);
            }
            """], ["nameof.cs"]);

        var syntaxWarnings = result.Warnings
            .Where(w => w.Contains(TinyCsDiagnosticIds.UnsupportedSyntax))
            .ToArray();

        Assert.True(result.Success);
        Assert.Equal(3, result.Warnings.Count);
        Assert.Equal(3, syntaxWarnings.Length);
        Assert.All(syntaxWarnings,
            warning => Assert.Contains("NameOfExpression", warning));
        Assert.Equal([
            "nameof.cs(3,47): warning TCS1001: unsupported syntax: NameOfExpression",
            "nameof.cs(4,42): warning TCS1001: unsupported syntax: NameOfExpression",
            "nameof.cs(5,40): warning TCS1001: unsupported syntax: NameOfExpression",
        ], syntaxWarnings);
        Assert.Equal(3, result.Lua.Split(
            "--[[ unsupported: NameOfExpression ]]",
            StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("nameof(", result.Lua);
        Assert.Equal("value|E|DateTime", TestHelper.RunLua($$"""
            {{result.Lua}}
            print(tostring(NameDemo.Simple(1)) .. "|" ..
                tostring(NameDemo.MemberName()) .. "|" ..
                tostring(NameDemo.TypeName()))
            """).Trim());
    }

    [Fact]
    public void UserMethodNamedNameof_IsNotNameOfExpression()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class NameDemo
            {
                public static string nameof(string value) => value;
                public static string Run() => nameof("ok");
            }
            """], checkNaming: false);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Warnings,
            warning => warning.Contains(TinyCsDiagnosticIds.UnsupportedSyntax));
        Assert.Equal("ok", TestHelper.RunLua(
            $"{result.Lua}\nprint(NameDemo.Run())").Trim());
    }

    [Fact]
    public void UnsupportedBclApi_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System.IO;

            public class T
            {
                public static string Test()
                {
                    return File.ReadAllText("save.dat");
                }
            }
            """]);

        Assert.True(result.Success);
        Assert.Contains(result.Warnings,
            w => w.Contains(TinyCsDiagnosticIds.UnsupportedApi)
                && w.Contains("System.IO.File.ReadAllText"));
    }

    [Fact]
    public void FullyQualifiedSupportedApi_HasNoUnsupportedApiWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static int Test()
                {
                    return System.Math.Min(3, 7);
                }
            }
            """]);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Warnings,
            warning => warning.Contains(TinyCsDiagnosticIds.UnsupportedApi));
    }

    [Fact]
    public void FullyQualifiedUnsupportedApi_ReportsSingleMemberWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static string Test()
                {
                    return System.IO.File.ReadAllText("save.dat");
                }
            }
            """]);

        Assert.True(result.Success);
        var warning = Assert.Single(result.Warnings,
            value => value.Contains(TinyCsDiagnosticIds.UnsupportedApi));
        Assert.Contains("System.IO.File.ReadAllText", warning);
    }

    [Fact]
    public void UnsupportedCoreLibraryMembers_ReportWarnings()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class T
            {
                public static double Test()
                {
                    var values = new List<int> { 1 };
                    values.Reverse();
                    var one = values.Single();
                    var capacity = values.Capacity;
                    var empty = string.Empty;
                    return Math.Cbrt(one) + Math.E + capacity + empty.Length;
                }
            }
            """]);

        var apiWarnings = result.Warnings
            .Where(w => w.Contains(TinyCsDiagnosticIds.UnsupportedApi))
            .ToArray();

        Assert.True(result.Success);
        Assert.Equal(6, apiWarnings.Length);
        Assert.Contains(apiWarnings, w => w.Contains("List<T>.Reverse"));
        Assert.Contains(apiWarnings,
            w => w.Contains("System.Linq.Enumerable.Single"));
        Assert.Contains(apiWarnings, w => w.Contains("System.Math.Cbrt"));
        Assert.Contains(apiWarnings, w => w.Contains("List<T>.Capacity"));
        Assert.Contains(apiWarnings, w => w.Contains("string.Empty"));
        Assert.Contains(apiWarnings, w => w.Contains("System.Math.E"));
    }

    [Fact]
    public void UnsupportedCollectionNulls_ReportWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System.Collections.Generic;
            using System.Linq;

            public class T
            {
                public static void Test()
                {
                    var list = new List<string> { null };
                    list.Add(default);
                    list[0] = null;

                    var dict = new Dictionary<string, string>
                    {
                        { "a", null }
                    };
                    dict["b"] = default;

                    var values = new List<string> { "a" };
                    var byValue = values.ToDictionary(v => v, v => (string)null);
                }
            }
            """]);

        var collectionNulls = result.Warnings
            .Where(w => w.Contains("unsupported collection null"))
            .ToArray();

        Assert.True(result.Success);
        Assert.Equal(6, collectionNulls.Length);
        Assert.Contains(collectionNulls, w => w.Contains("List<T>"));
        Assert.Contains(collectionNulls, w => w.Contains("Dictionary<K,V>"));
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
}
