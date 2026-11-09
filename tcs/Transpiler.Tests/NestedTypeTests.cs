namespace TinyCs.Tests;

// 入れ子の型の名前の写像と、generic な型の中の型の診断。C、VM、.NET の実行結果は tcs2c.Tests の NestedTypes
public class NestedTypeTests
{
    [Fact]
    public void GlobalName_JoinsOuterTypes()
    {
        const string source = """
            namespace Game
            {
                public class Outer
                {
                    public class Inner { public class Deep { public static int V = 3; } }
                    public enum Kind { A = 2 }
                }
            }
            """;
        var export = IlExport.Export([source]);
        Assert.Empty(export.Diagnostics);
        Assert.Contains(export.Classes, c => c.Name == "Game_Outer_Inner_Deep");
        Assert.Contains(export.EnumTypes, e => e.Name == "Game_Outer_Kind");
        Assert.Equal("3:2", TestHelper.CompileAndRunVm(source, "Game.Outer.Inner.Deep.V + \":\" + (int)Game.Outer.Kind.A"));
    }

    [Theory]
    [InlineData("public class G<T> { public class Inner { public T V; } }")]
    [InlineData("public class G<T> { public struct Inner { public T V; } }")]
    [InlineData("public class G<T> { public enum Kind { A } }")]
    [InlineData("public struct G<T> { public class Inner { } }")]
    [InlineData("public class G<T> { public class M { public class Deep { } } }")]
    public void TypeInGenericType_IsRejected(string source)
    {
        var result = Checker.Check([source]);
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
        var result = Checker.Check([source]);
        Assert.Contains(result.Errors, e => e.Contains(rule));
    }
}
