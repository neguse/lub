namespace TinyCs.Tests;

public class StringEdgeContractTests
{
    [Fact]
    public void String_Replace_EmptyOldValueFailsImmediatelyLikeDotNet()
    {
        Assert.Throws<ArgumentException>(() => "abc".Replace("", "x"));

        var error = Assert.Throws<InvalidOperationException>(() =>
            TestHelper.TranspileAndRunWithRuntime("""
                public class T
                {
                    public static string Test() => "abc".Replace("", "x");
                }
                """, "T.Test()", TimeSpan.FromSeconds(3)));

        Assert.Contains("oldValue", error.Message);
    }

    [Fact]
    public void String_EndsWith_EmptySuffixMatchesDotNet()
    {
        var expected = "hello".EndsWith("").ToString().ToLowerInvariant();

        var actual = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static bool Test() => "hello".EndsWith("");
            }
            """, "tostring(T.Test())");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void String_Split_EmptySeparatorReturnsOriginalSingleElement()
    {
        const string input = "a b";
        var expectedParts = input.Split("");
        var expectedEmptyParts = "".Split("");
        var expected = $"{expectedParts.Length}|{expectedParts[0]}|" +
            $"{expectedEmptyParts.Length}|{expectedEmptyParts[0]}";

        var actual = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    var parts = "a b".Split("");
                    var emptyParts = "".Split("");
                    return parts.Length + "|" + parts[0] + "|" +
                        emptyParts.Length + "|" + emptyParts[0];
                }
            }
            """, "T.Test()");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void String_Split_NoSeparatorUsesWhitespaceAndPreservesEmptyEntries()
    {
        const string input = " a\tb\r\nc\v\fd ";
        var expectedParts = input.Split();
        var expected = $"{expectedParts.Length}|{string.Join("|", expectedParts)}";

        var actual = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    var parts = " a\tb\r\nc\v\fd ".Split();
                    return parts.Length + "|" + parts[0] + "|" + parts[1]
                        + "|" + parts[2] + "|" + parts[3] + "|" + parts[4]
                        + "|" + parts[5] + "|" + parts[6] + "|" + parts[7];
                }
            }
            """, "T.Test()");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void String_Split_NoSeparatorOnEmptyReturnsSingleEmptyElement()
    {
        var expectedParts = "".Split();
        var expected = $"{expectedParts.Length}|{expectedParts[0]}";

        var actual = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    var parts = "".Split();
                    return parts.Length + "|" + parts[0];
                }
            }
            """, "T.Test()");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void String_Split_NullStringSeparatorReturnsOriginalSingleElement()
    {
        const string input = "a b";
        var expectedParts = input.Split((string?)null);
        var expected = $"{expectedParts.Length}|{expectedParts[0]}";

        var actual = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    var parts = "a b".Split((string?)null);
                    return parts.Length + "|" + parts[0];
                }
            }
            """, "T.Test()");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void String_Join_EmptyValuesMatchDotNet()
    {
        var expected = string.Join("", "", "a", "") + "|" +
            string.Join(",", Array.Empty<string>());

        var actual = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    var empty = new string[] { };
                    return string.Join("", "", "a", "") + "|" +
                        string.Join(",", empty);
                }
            }
            """, "T.Test()");

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void String_EmptyInputSupportedMembersMatchDotNet()
    {
        const string empty = "";
        var expected = 0;
        if (empty.Contains("")) expected++;
        if (empty.IndexOf("") == 0) expected++;
        if (empty.StartsWith("")) expected++;
        if (empty.EndsWith("")) expected++;
        if (empty.Trim().Length == 0) expected++;
        if (empty.Substring(0).Length == 0) expected++;
        if (empty.Substring(0, 0).Length == 0) expected++;
        if (empty.Replace("x", "y").Length == 0) expected++;
        if (empty.IndexOf("", 0) == 0) expected++;
        if (empty.ToUpper().Length == 0) expected++;
        if (empty.ToLower().Length == 0) expected++;
        if (empty.ToString().Length == 0) expected++;

        var actual = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static int Test()
                {
                    string empty = "";
                    int result = 0;
                    if (empty.Contains("")) result++;
                    if (empty.IndexOf("") == 0) result++;
                    if (empty.StartsWith("")) result++;
                    if (empty.EndsWith("")) result++;
                    if (empty.Trim().Length == 0) result++;
                    if (empty.Substring(0).Length == 0) result++;
                    if (empty.Substring(0, 0).Length == 0) result++;
                    if (empty.Replace("x", "y").Length == 0) result++;
                    if (empty.IndexOf("", 0) == 0) result++;
                    if (empty.ToUpper().Length == 0) result++;
                    if (empty.ToLower().Length == 0) result++;
                    if (empty.ToString().Length == 0) result++;
                    return result;
                }
            }
            """, "T.Test()");

        Assert.Equal(expected.ToString(), actual);
    }
}
