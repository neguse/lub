namespace TinyCs.Tests;

// enum の文字列化 (#133)。--ref の host enum も stub の member 名を出す。.NET との一致は tcs2c.Tests の
// DifferentialTests.EnumToString
public class EnumToStringTests
{
    [Fact]
    public void ReferenceEnum_UsesStubMemberNames()
    {
        var output = VmCorpus.RunPrograms([
            """
            public static class P
            {
                public static string Test() => Host.Mode.On + "|" + (Host.Mode)5;
            }
            """,
            "public static class TcsEntry { public static void Main() { System.Console.WriteLine(P.Test()); } }",
        ], "TcsEntry", refs: ["""
            namespace Host { public enum Mode { Off = 0, On = 3 } }
            """]);
        Assert.Equal("On|5", output);
    }

    // 書式は "G" / "F" / "D" の定数だけ。X 等と定数でない書式は TCS1002
    [Fact]
    public void UnsupportedFormat_ReportsUnsupportedApi()
    {
        var result = Checker.Check(["""
            public enum Color { Red, Green }
            public static class P
            {
                public static string Test(string f) =>
                    Color.Green.ToString("G") + Color.Green.ToString("d")
                    + Color.Green.ToString("X") + Color.Green.ToString(f);
            }
            """]);

        var api = result.Errors.Where(e => e.Contains("TCS1002")).ToList();
        Assert.Equal(2, api.Count);
        Assert.All(api, e => Assert.Contains("unsupported API: System.Enum.ToString", e));
        Assert.StartsWith("(6,11)", api[0]);
        Assert.StartsWith("(6,39)", api[1]);
    }
}
