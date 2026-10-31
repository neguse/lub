namespace TinyCs.Tests;

// 入れ子の型の名前の写像と、generic な型の中の型の診断。3 経路の実行結果は tcs2c.Tests の NestedTypes
public class NestedTypeTests
{
    private const string Source = """
        public class Outer
        {
            public static int Value() { return 1; }
            public class Inner
            {
                public static int Value() { return 10; }
                public static int Read() { return Value(); }
            }
            public struct Part
            {
                public int V;
                public int Value() { return V + 100; }
            }
            public static int Run()
            {
                var p = new Part();
                return Value() + Inner.Read() + p.Value();
            }
        }
        """;

    [Fact]
    public void GlobalName_JoinsOuterTypes()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            namespace Game
            {
                public class Outer
                {
                    public class Inner { public class Deep { public static int V = 3; } }
                    public enum Kind { A = 2 }
                }
            }
            """]);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Equal("3:2", TestHelper.RunLua(
            $"{result.Lua}\nprint(Game_Outer_Inner_Deep.V .. \":\" .. Game_Outer_Kind.A)").Trim());
    }

    // 本文の編集の差し替えは、その method を直接持つ型の範囲だけを替える
    [Theory]
    [InlineData("return 10;", "return 20;", "111", "121")]
    [InlineData("return V + 100;", "return V + 200;", "111", "211")]
    [InlineData("return 1;", "return 2;", "111", "112")]
    public void BodyEdit_ReplacesTheMethodOfTheOwningType(string before, string after,
        string expectedBefore, string expectedAfter)
    {
        var session = new IncrementalCompilationSession();
        session.OpenProject([("a.cs", Source)]);
        string Run() => TestHelper.RunLua(
            string.Join("", session.Artifacts.Select(a => a.Lua)) + "\nprint(Outer.Run())").Trim();
        Assert.Equal(expectedBefore, Run());
        var result = session.Update("a.cs", Source.Replace(before, after));
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Equal(expectedAfter, Run());
    }

    [Fact]
    public void Snapshot_RegistersNestedTypeUnderItsGlobalName()
    {
        var session = new IncrementalCompilationSession();
        session.OpenProject([("a.cs", Source)]);
        var output = StaticInitOrderTests.RunSnapshot(StaticInitOrderTests.Snapshot(session),
            """print(reg.types["a.cs#Outer_Inner"].Read())""");
        Assert.Equal(["10"], output);
    }

    [Theory]
    [InlineData("public class G<T> { public class Inner { public T V; } }")]
    [InlineData("public class G<T> { public struct Inner { public T V; } }")]
    [InlineData("public class G<T> { public enum Kind { A } }")]
    [InlineData("public struct G<T> { public class Inner { } }")]
    [InlineData("public class G<T> { public class M { public class Deep { } } }")]
    public void TypeInGenericType_IsRejected(string source)
    {
        var result = Transpiler.TranspileWithDiagnostics([source]);
        Assert.Contains(result.Errors, e => e.Contains("TCS1001") && e.Contains("NestedTypeInGenericType"));
    }

    [Theory]
    [InlineData("public class O { public class A_B { } }", "UnderscoreTypeName(A_B)")]
    [InlineData("public class O { public partial class P { } }", "PartialTypeDeclaration")]
    [InlineData("public class O { public abstract class P { } }", "AbstractClass")]
    [InlineData("public class O { public class B { } public class D : B { } }", "ClassInheritance")]
    [InlineData("public class O { public delegate void D(); }", "DelegateDeclaration")]
    public void NestedDeclaration_FollowsTheSameRules(string source, string rule)
    {
        var result = Transpiler.TranspileWithDiagnostics([source]);
        Assert.Contains(result.Errors, e => e.Contains(rule));
    }
}
