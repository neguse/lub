namespace TinyCs.Tests;

public class StringSplitSemanticTests
{
    [Fact]
    public void String_Split_Comma()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static int Test()
                {
                    string s = "a,b,c";
                    var parts = s.Split(",");
                    return parts.Length;
                }
            }
            """, "T.test()");
        Assert.Equal("3", result);
    }

    [Fact]
    public void String_Split_Content()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    string s = "hello,world";
                    var parts = s.Split(",");
                    return parts[0];
                }
            }
            """, "T.test()");
        Assert.Equal("hello", result);
    }
}
