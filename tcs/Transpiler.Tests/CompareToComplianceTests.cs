namespace TinyCs.Tests;

// CompareTo は int / float の同じ型どうし (intrinsic の Math.CompareTo) だけが
// サブセット。それ以外の形は emit / 実行時に落ちる前に TCS1002 で止める
public class CompareToComplianceTests
{
    private static string Wrap(string statement) => $$"""
        using System;

        public enum Color { Red, Green }
        public class Box<T> where T : IComparable<T>
        {
            public T Value;
            public int Cmp(T other) => Value.CompareTo(other);
        }
        public class Mine
        {
            public int CompareTo(Mine other) => 0;
        }
        public class Api
        {
            public static void Run()
            {
                int i = 3;
                float f = 1.5f;
                int? n = 4;
                var c = Color.Green;
                IComparable<int> boxed = i;
                {{statement}}
            }
        }
        """;

    [Theory]
    [InlineData("var r = i.CompareTo((object)5);", "int.CompareTo")]
    [InlineData("var r = i.CompareTo(5L);", "int.CompareTo")]
    [InlineData("var r = i.CompareTo(5u);", "int.CompareTo")]
    [InlineData("var r = c.CompareTo(Color.Red);", "System.Enum.CompareTo")]
    [InlineData("var r = boxed.CompareTo(5);", "System.IComparable<T>.CompareTo")]
    [InlineData("var r = 'a'.CompareTo('b');", "char.CompareTo")]
    [InlineData("var r = \"a\".CompareTo(\"b\");", "string.CompareTo")]
    public void UnhandledForm_ReportsTcs1002(string statement, string expectedApiName)
    {
        var result = Transpiler.TranspileWithDiagnostics([Wrap(statement)]);

        Assert.Contains(result.Errors, e => e.Contains(TinyCsDiagnosticIds.UnsupportedApi)
            && e.Contains(expectedApiName));
    }

    [Fact]
    public void GenericComparableReceiver_ReportsTcs1002()
    {
        var result = Transpiler.TranspileWithDiagnostics([Wrap("")]);

        Assert.Single(result.Errors, e => e.Contains(TinyCsDiagnosticIds.UnsupportedApi)
            && e.Contains("System.IComparable<T>.CompareTo"));
    }

    [Fact]
    public void IntFloatAndUserCompareTo_AreSupported()
    {
        var source = Wrap("""
            var r = i.CompareTo(2) + f.CompareTo(2.5f) + f.CompareTo(1) + (n?.CompareTo(5) ?? 0)
                + new Mine().CompareTo(new Mine());
            """).Replace("public int Cmp(T other) => Value.CompareTo(other);", "")
            .Replace("where T : IComparable<T>", "").Replace("IComparable<int> boxed = i;", "");
        var result = Transpiler.TranspileWithDiagnostics([source]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
    }
}
