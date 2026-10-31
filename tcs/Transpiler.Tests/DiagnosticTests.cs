namespace TinyCs.Tests;

public class DiagnosticTests
{
    private static void AssertUnsupportedError(TranspileResult result, string kind)
    {
        Assert.False(result.Success);
        Assert.Contains(result.Errors,
            e => e.Contains("unsupported") && e.Contains(kind));
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

    // user の member は C# の識別子のまま Lua の名前になり、Lua の予約語の member は
    // 宣言で error になる。`_` を後置した綴り (`end_`) は普通の名前で、予約語の
    // member と並んでも error は予約語の側だけ
    [Theory]
    [InlineData("public int end_; public void end() { }", "end")]
    [InlineData("public int end { get; set; } public void end_() { }", "end")]
    [InlineData("public int local_; public int local { get; set; }", "local")]
    public void LuaKeywordMember_BesideUnderscoreName_IsTheOnlyError(string members,
        string keyword)
    {
        var result = Transpiler.TranspileWithDiagnostics(
            [$"public class Repro {{ {members} }}"], ["Repro.cs"]);
        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.StartsWith("Repro.cs(1,", error);
        Assert.Contains($"error TCS1001: unsupported syntax: LuaKeywordIdentifier({keyword})",
            error);
        Assert.Equal("", result.Lua);
    }

    // 大文字小文字だけ違う member は別の Lua 名 (`flash` と `Flash`) で共存する
    [Fact]
    public void MembersDifferingOnlyInCase_Coexist()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Repro
            {
                public int flash;
                public void Flash() { flash = flash + 1; }
                public static int Test()
                {
                    var r = new Repro();
                    r.Flash();
                    r.Flash();
                    return r.flash;
                }
            }
            """, "Repro.Test() .. ',' .. type(Repro.new().flash) .. ',' .. type(Repro.Flash)");
        Assert.Equal("2,number,function", result);
    }

    [Fact]
    public void SameNameInstanceOverload_IsSupported()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Repro
            {
                public int Flash() { return 1; }
                public int Flash(int n) { return n; }
                public static int Test() { var r = new Repro(); return r.Flash() + r.Flash(5); }
            }
            """, "Repro.Test()");
        Assert.Equal("6", result);
    }

    [Fact]
    public void RecordPositionalParameter_BesideKeywordField_KeywordIsTheOnlyError()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public record Repro(int end_)
            {
                public int end;
            }
            """]);
        Assert.False(result.Success);
        var error = Assert.Single(result.Errors);
        Assert.Contains("LuaKeywordIdentifier(end)", error);
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
    [InlineData("public class T { public static int Test() { int x = 1.5f; return x; } }", "CS0266")]
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
    public void DataStruct_NoDiagnostic()
    {
        // field のみのデータ struct はサブセット内
        var result = Transpiler.TranspileWithDiagnostics(["""
            public struct Vec2
            {
                public int X;
            }
            """]);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains("StructDeclaration"));
    }

    // record struct は対応済み。static member は struct と同じく
    // サブセット外のまま
    [Fact]
    public void RecordStruct_IsClean_IndexerReportsWarning()
    {
        var clean = Transpiler.TranspileWithDiagnostics(["""
            public readonly record struct Vec2(int X, int Y);
            """]);
        Assert.DoesNotContain(clean.Warnings,
            w => w.Contains("RecordStruct") || w.Contains("StructMember"));

        var withIndexer = Transpiler.TranspileWithDiagnostics(["""
            public record struct Vec2(int X, int Y)
            {
                public int this[int i] { get { return X; } }
            }
            """]);
        AssertUnsupportedError(withIndexer, "StructMember");
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

        AssertUnsupportedError(result, "ThrowStatement");
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

        AssertUnsupportedError(result, "TryStatement");
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

        AssertUnsupportedError(result, "TryStatement");
        AssertUnsupportedError(result, "ThrowStatement");
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

        AssertUnsupportedError(result, "UsingStatement");
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

        AssertUnsupportedError(result, "UsingDeclaration");
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

        AssertUnsupportedError(result, "LocalFunctionStatement");
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

        AssertUnsupportedError(result, "ListPattern");
    }

    [Fact]
    public void PartialTypesAndLock_AreErrors()
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

        Assert.False(result.Success);
        Assert.Equal("", result.Lua);
        Assert.Equal(5, result.Errors.Count);
        Assert.Equal(4, result.Errors.Count(
            error => error.Contains("PartialTypeDeclaration")));
        Assert.Single(result.Errors,
            error => error.Contains("LockStatement"));
    }

    // nameof は C# の定数式 (識別子名) として両 backend が文字列に畳む (T250)
    [Fact]
    public void NameOfExpressions_AreConstantStrings()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class NameDemo
            {
                public static string Simple(int value) => nameof(value);
                public static string MemberName() => nameof(System.Math.E);
                public static string TypeName() => nameof(System.DateTime);
            }
            """], ["nameof.cs"]);

        Assert.True(result.Success);
        Assert.Empty(result.Warnings);
        Assert.DoesNotContain("nameof(", result.Lua);
        Assert.DoesNotContain("unsupported", result.Lua);
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
            """]);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Warnings,
            warning => warning.Contains(TinyCsDiagnosticIds.UnsupportedSyntax));
        Assert.Equal("ok", TestHelper.RunLua(
            $"{result.Lua}\nprint(NameDemo.Run())").Trim());
    }

    [Fact]
    public void UnsupportedBclApi_ReportsError()
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

        Assert.False(result.Success);
        Assert.Contains(result.Errors,
            e => e.Contains(TinyCsDiagnosticIds.UnsupportedApi)
                && e.Contains("System.IO.File.ReadAllText"));
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
    public void FullyQualifiedUnsupportedApi_ReportsSingleMemberError()
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

        Assert.False(result.Success);
        var error = Assert.Single(result.Errors,
            value => value.Contains(TinyCsDiagnosticIds.UnsupportedApi));
        Assert.Contains("System.IO.File.ReadAllText", error);
    }

    [Fact]
    public void UnsupportedCoreLibraryMembers_ReportErrors()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class T
            {
                public static float Test()
                {
                    var values = new List<int> { 1 };
                    values.Reverse();
                    var one = values.Single();
                    var capacity = values.Capacity;
                    var empty = string.Empty;
                    return (float)(Math.Cbrt(one) + Math.E + capacity + empty.Length);
                }
            }
            """]);

        var apiErrors = result.Errors
            .Where(e => e.Contains(TinyCsDiagnosticIds.UnsupportedApi))
            .ToArray();

        Assert.False(result.Success);
        Assert.Equal(7, apiErrors.Length);
        Assert.Contains(apiErrors, w => w.Contains("List<T>.Reverse"));
        Assert.Contains(apiErrors,
            w => w.Contains("System.Linq.Enumerable.Single"));
        Assert.Contains(apiErrors, w => w.Contains("System.Math.Cbrt"));
        Assert.Contains(apiErrors, w => w.Contains("List<T>.Capacity"));
        Assert.Contains(apiErrors, w => w.Contains("string.Empty"));
        Assert.Contains(apiErrors, w => w.Contains("System.Math.E"));
    }

    [Fact]
    public void UnsupportedCollectionNulls_ReportWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System.Collections.Generic;

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
                }
            }
            """]);

        var collectionNulls = result.Warnings
            .Where(w => w.Contains("unsupported collection null"))
            .ToArray();

        Assert.True(result.Success);
        Assert.Equal(2, collectionNulls.Length);
        Assert.All(collectionNulls, w => Assert.Contains("Dictionary<K,V>", w));
    }

    // #20: 要素の default が nil になる `new T[n]` は Lua で `{}` (Length 0、
    // foreach 0 回) になるので TCS1003。初期化子の null 要素と `a[i] = null`
    // も同じ nil 制約。値型要素 (struct / enum / char / bool) は default で
    // 詰まるので対象外。`default(int)` は int? / object へ変換しても非 null
    public const string ArrayNullSource = """
        using System.Collections.Generic;
        public class Foo { public int V; }
        public struct S { public int X; }
        public enum E { A = 3 }
        public class Pool<T> { public T[] Items = new T[2]; }
        public class Box<T> where T : struct { public T[] Make(int n) => new T[n]; }
        public class Bag<T>
        {
            public T[] Make(T x) => new T[] { x, default, x };
            public void Clear(T[] a) { a[0] = default; }
            public void Push(List<T> l) { l.Add(default); }
        }
        public class Demo
        {
            public static void Run()
            {
                var strings = new string[3];
                var foos = new Foo[2];
                var maybe = new int?[2];
                var jagged = new int[2][];
                var withNull = new string[] { "a", null };
                var implicitNull = new[] { "a", (string)null };
                strings[0] = null;

                var ints = new int[3];
                var structs = new S[2];
                var enums = new E[2];
                var chars = new char[2];
                var flags = new bool[2];
                var fine = new string[] { "a", "b" };
                fine[0] = "c";
                var inner = new int[][] { new int[2], new int[3] };
                var liftedZero = new int?[] { default(int) };
                var boxedZero = new object[] { default(int) };
                var list = new List<string>();
            }
        }
        """;

    [Fact]
    public void SizedArraysWithNilDefault_ReportCollectionNullWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics([ArrayNullSource]);

        var collectionNulls = result.Warnings
            .Where(w => w.Contains("TCS1003"))
            .ToArray();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Warnings, w => !w.Contains("TCS1003"));
        Assert.Equal(11, collectionNulls.Length);
        Assert.All(collectionNulls, w => Assert.Contains("Lua sequence tables", w));
        Assert.Contains(collectionNulls, w => w.Contains("string[] created by size"));
        Assert.Contains(collectionNulls, w => w.Contains("Foo[] created by size"));
        Assert.Contains(collectionNulls, w => w.Contains("int?[] created by size"));
        Assert.Contains(collectionNulls, w => w.Contains("int[][] created by size"));
        Assert.Equal(3, collectionNulls.Count(
            w => w.Contains("string[] cannot store null elements")));
        Assert.Equal(2, collectionNulls.Count(
            w => w.Contains("T[] created by size")));
        Assert.Equal(2, collectionNulls.Count(
            w => w.Contains("T[] cannot store null elements")));
        Assert.DoesNotContain(collectionNulls, w => w.Contains("List<T>"));
    }
}
