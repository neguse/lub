namespace TinyCs.Tcs2c.Tests;

// Type tests of numbers (int / float) stored in an object, and patterns that read the value of a number or bool stored in an object, are outside the subset
// (TCS1001 BoxedValuePattern(T)). Values are extracted with a cast (`(int)o`). `o is bool`, string and class patterns, and patterns on a T? input are not affected
public class BoxedValuePatternDifferentialTests
{
    private static string BoxedValueProgram(string statement) => $$"""
        public static class P
        {
            static object O(object x) => x;
            public static void Main()
            {
                object o = O(5);
                object f = O(1.5f);
                object b = O(true);
                {{statement}}
            }
        }
        """;

    [Theory]
    [InlineData("System.Console.WriteLine(o is int);", "BoxedValuePattern(int)")]
    [InlineData("System.Console.WriteLine(o is float);", "BoxedValuePattern(float)")]
    [InlineData("System.Console.WriteLine(o is not int);", "BoxedValuePattern(int)")]
    [InlineData("System.Console.WriteLine(o is int or float);", "BoxedValuePattern(int)")]
    [InlineData("System.Console.WriteLine(o switch { int => 1, _ => 0 });", "BoxedValuePattern(int)")]
    [InlineData("System.Console.WriteLine(o is int x ? x : -1);", "BoxedValuePattern(int)")]
    [InlineData("System.Console.WriteLine(f is float y ? y : -1f);", "BoxedValuePattern(float)")]
    [InlineData("System.Console.WriteLine(o is (int w) ? w : 0);", "BoxedValuePattern(int)")]
    [InlineData("System.Console.WriteLine(o switch { int n => n, _ => 0 });", "BoxedValuePattern(int)")]
    [InlineData("System.Console.WriteLine(o switch { int n when n > 0 => n, _ => 0 });", "BoxedValuePattern(int)")]
    [InlineData("switch (o) { case int n: System.Console.WriteLine(n); break; }", "BoxedValuePattern(int)")]
    [InlineData("System.Console.WriteLine(o is int and > 3);", "BoxedValuePattern(int)")]
    [InlineData("System.Console.WriteLine(o is 5);", "BoxedValuePattern(int)")]
    [InlineData("System.Console.WriteLine(f is 1.5f);", "BoxedValuePattern(float)")]
    [InlineData("System.Console.WriteLine(o is not 5);", "BoxedValuePattern(int)")]
    [InlineData("System.Console.WriteLine(o switch { 5 => 1, _ => 0 });", "BoxedValuePattern(int)")]
    [InlineData("System.Console.WriteLine(b is bool z && z);", "BoxedValuePattern(bool)")]
    [InlineData("System.Console.WriteLine(b is true);", "BoxedValuePattern(bool)")]
    [InlineData("System.Console.WriteLine(b is bool and true);", "BoxedValuePattern(bool)")]
    public void BoxedValuePattern_BothBackendsStopWithDiagnostic(string statement, string rule)
    {
        var program = BoxedValueProgram(statement);
        foreach (var run in new Func<object>[]
                 {
                     () => Backends.EmitVm([program], "P"),
                     () => Backends.EmitC([program], "P"),
                 })
        {
            var ex = Assert.Throws<InvalidOperationException>(run);
            Assert.Contains($"error TCS1001: unsupported syntax: {rule}", ex.Message);
        }
    }

    // Values are extracted with a cast. class, null, and T? constant patterns work as before
    [CFact]
    public void BoxedValueByCast_AndReferencePatterns_StillRun()
    {
        var output = Backends.AssertParity("""
            public class C { public int N = 2; }
            public static class P
            {
                static object O(object x) => x;
                public static void Main()
                {
                    object o = O(5);
                    object f = O(1.5f);
                    object c = O(new C());
                    int? some = 7;
                    var k = c is C cc ? cc.N : -1;
                    System.Console.WriteLine(((int)o + 1) + "," + ((float)f + 1f) + "," + k + "," + (some is 7) + "," + (o is null));
                }
            }
            """, "P");
        Assert.Equal("6,2.5,2,True,False", output);
    }

    // Patterns on a T? input and type tests for bool and string in an object are not affected. The C backend does not have these type tests, so they are checked on the VM and .NET
    [CFact]
    public void NullableBoolAndStringPatterns_StillRunOnVm()
    {
        const string source = """
            public static class P
            {
                static object O(object x) => x;
                public static void Main()
                {
                    object o = O(5);
                    object b = O(true);
                    object s = O("t");
                    int? some = 7;
                    int? none = null;
                    var a = some is int x ? x : -1;
                    var n = none switch { int y => y, _ => -1 };
                    var u = s is string w ? w : "-";
                    System.Console.WriteLine(a + "," + n + "," + (b is bool) + "," + (o is bool) + "," + (s is string) + "," + (o is string) + "," + (s is "t") + "," + u);
                }
            }
            """;
        Assert.Equal("7,-1,True,False,True,False,True,t", Backends.RunDotnet(source, "P"));
        Assert.Equal("7,-1,True,False,True,False,True,t", Backends.RunVm([source], "P"));
    }
}
