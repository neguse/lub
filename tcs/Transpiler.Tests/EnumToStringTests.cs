namespace TinyCs.Tests;

// enum の文字列化 (#133)。名前の表は文字列化する地点にだけ出し、--ref の host
// enum も stub の member 名を出す。.NET との一致は tcs2c.Tests の
// DifferentialTests.EnumToString
public class EnumToStringTests
{
    [Fact]
    public void NameTable_IsEmittedOnlyWhereStringified()
    {
        var lua = Transpiler.Transpile("""
            public enum Color { Red, Green }
            public enum Dir { Up, Down }
            public static class P
            {
                public static int Turn(Dir d) => d == Dir.Up ? 1 : 0;
                public static string Test() => "c=" + Color.Green;
            }
            """);

        Assert.Contains("\"Red:0,Green:1\"", lua);
        Assert.DoesNotContain("Up:", lua);
    }

    [Fact]
    public void ReferenceEnum_UsesStubMemberNames()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public static class P
            {
                public static string Test() => Host.Mode.On + "|" + (Host.Mode)5;
            }
            """], referenceSources: ["""
            namespace Host { public enum Mode { Off = 0, On = 3 } }
            """]);
        Assert.True(result.Success, string.Join("\n", result.Errors));

        var runtimePath = TestHelper.FindProjectFile("runtime/tinysystem.lua");
        Assert.Equal("On|5", TestHelper.RunLua(
            $"local TinySystem = dofile(\"{runtimePath}\")\nString = TinySystem.String\n"
            + "host = { OFF = 0, ON = 3 }\n"
            + $"{result.Lua}\nprint(P.Test())").Trim());
    }

    // host が enum を数でない Lua の値で持つとき (lub の [LubLuaString] 等) は
    // その値を出す (Flags でも bit 演算に渡さない)
    [Fact]
    public void ReferenceEnumWithNonNumberValue_PrintsLuaValue()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public static class P
            {
                public static string Test() => "" + Host.Mode.B;
            }
            """], referenceSources: ["""
            namespace Host { [System.Flags] public enum Mode { A = 1, B = 2 } }
            """]);
        Assert.True(result.Success, string.Join("\n", result.Errors));

        var runtimePath = TestHelper.FindProjectFile("runtime/tinysystem.lua");
        Assert.Equal("b", TestHelper.RunLua(
            $"local TinySystem = dofile(\"{runtimePath}\")\nString = TinySystem.String\n"
            + "host = { A = \"a\", B = \"b\" }\n"
            + $"{result.Lua}\nprint(P.Test())").Trim());
    }

    // 書式は "G" / "F" / "D" の定数だけ。X 等と定数でない書式は TCS1002
    [Fact]
    public void UnsupportedFormat_ReportsUnsupportedApi()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
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
