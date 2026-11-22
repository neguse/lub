namespace TinyCs.Tests;

// Enum stringification (#133). --ref host enums also print the stub member names. Matching .NET is covered by tcs2c.Tests
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

    // Formats are limited to the constants "G" / "F" / "D". X and other non-constant formats are TCS1002
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
