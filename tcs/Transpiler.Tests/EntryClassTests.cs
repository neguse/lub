namespace TinyCs.Tests;

public class EntryClassTests
{
    private const string AppSource = """
        public static class App
        {
            public static string Ping()
            {
                return "pong";
            }
        }
        """;

    [Fact]
    public void EntryClass_Resolves()
    {
        var result = Checker.Check([AppSource], entryClass: "App");

        Assert.True(result.Success, string.Join("\n", result.Errors));
    }

    [Fact]
    public void EntryClass_NotFound_ReportsError()
    {
        var result = Checker.Check([AppSource],
            entryClass: "Missing");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("entry class not found"));
    }

    [Fact]
    public void EntryClass_ReferenceOnlyType_ReportsError()
    {
        var refSource = """
            public static class Host
            {
                public static void ping() { }
            }
            """;

        var result = Checker.Check([AppSource], null,
            [refSource], entryClass: "Host");

        Assert.False(result.Success);
        Assert.Contains(result.Errors,
            e => e.Contains("reference-only") && e.Contains("Host"));
    }

    // entry は namespaced 指定・一意な simple 指定の両方で引け、
    // interface / 曖昧な simple 名は誤り。
    private const string NamespacedSource = """
        namespace Game
        {
            public static class App
            {
                public static string Ping()
                {
                    return "pong";
                }
            }
        }
        """;

    [Fact]
    public void EntryClass_NamespaceQualified_Resolves()
    {
        var result = Checker.Check([NamespacedSource], entryClass: "Game.App");

        Assert.True(result.Success, string.Join("\n", result.Errors));
    }

    [Fact]
    public void EntryClass_UniqueSimpleNameInNamespace_Resolves()
    {
        var result = Checker.Check([NamespacedSource], entryClass: "App");

        Assert.True(result.Success, string.Join("\n", result.Errors));
    }

    [Fact]
    public void EntryClass_Interface_ReportsError()
    {
        var result = Checker.Check(["""
            public interface IApp
            {
                void Run();
            }
            public class App : IApp
            {
                public void Run() { }
            }
            """], entryClass: "IApp");

        Assert.False(result.Success);
        Assert.Contains(result.Errors,
            e => e.Contains("entry class must be a class"));
    }

    [Fact]
    public void EntryClass_AmbiguousSimpleName_ReportsError()
    {
        var result = Checker.Check(["""
            namespace Alpha
            {
                public static class App
                {
                    public static int N() => 1;
                }
            }
            namespace Beta
            {
                public static class App
                {
                    public static int N() => 2;
                }
            }
            """], entryClass: "App");

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("ambiguous"));
    }
}
