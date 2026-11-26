namespace TinyCs.Tests;

// Pins the IL shape of access to --ref (host) types (the source of the host function names in C and the VM).
// - A static call is a foreign function with a lowercase path including the namespace and nested types
// - An instance method is a foreign function carrying the receiver's type
// - A field read is a read of the host object's field
public class RefTypeAccessTests
{
    // A reference-only type inside a namespace is also looked up by a lowercase path including the namespace (Lub.Gfx → lub.gfx).
    [Fact]
    public void NamespacedRefTypes_UseNamespaceInForeignNames()
    {
        var refSource = """
            namespace Lub
            {
                public enum EventKind { Quit = 1 }
                public static class Gfx
                {
                    public enum PixelFormat { Rgba8 = 1 }
                    public static int Size() { return 0; }
                }
                public static class App { public static void Quit() { } }
            }
            """;
        var source = """
            using Lub;
            public static class Game
            {
                public static int Run()
                {
                    App.Quit();
                    return Gfx.Size() + (int)Gfx.PixelFormat.Rgba8 + (int)EventKind.Quit;
                }
            }
            """;

        var result = IlExport.Export([source], referenceSources: [refSource]);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(["lub.app.quit", "lub.gfx.size"],
            result.ForeignMethods.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    // Segments of a path mapped from a namespace are only lowercased, and are not changed even when spelled the same as a reserved word of another language (End → end).
    [Fact]
    public void RefPathSegment_IsLowercasedOnly()
    {
        var result = IlExport.Export(["""
            using End;
            public static class Game { public static int Run() { return Api.Get(); } }
            """], referenceSources:
            ["namespace End { public static class Api { public static int Get() { return 0; } } }"]);

        Assert.Empty(result.Diagnostics);
        Assert.Equal("end.api.get", Assert.Single(result.ForeignMethods).Name);
    }

    // A local with the same name as the head of a --ref path (such as hostapi from a namespace) can also be written.
    [Fact]
    public void LocalNamedLikeRefPathRoot_IsAccepted()
    {
        var result = Checker.Check(["""
            using HostApi;
            public static class Game
            {
                public static int Run() { var hostapi = 1; return hostapi + Screen.Get(); }
            }
            """], null,
            ["namespace HostApi { public static class Screen { public static int Get() { return 0; } } }"]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void RefTypeInstanceMethod_HasReceiver()
    {
        var refSource = """
            public class Readback
            {
                public string status()
                {
                    return "";
                }
            }

            public static class Gfx
            {
                public static Readback? readback()
                {
                    return null;
                }
            }
            """;
        var source = """
            public static class Game
            {
                public static string Run()
                {
                    var rb = Gfx.readback();
                    return rb!.status();
                }
            }
            """;

        var result = IlExport.Export([source], referenceSources: [refSource]);

        Assert.Empty(result.Diagnostics);
        var status = result.ForeignMethods.Single(m => m.Receiver == "Readback");
        Assert.EndsWith(".status", status.Name);
        Assert.Equal("string", status.ReturnType);
        Assert.Null(result.ForeignMethods.Single(m => m.Name == "gfx.readback").Receiver);
    }

    [Fact]
    public void RefTypeField_ReadIsAccepted()
    {
        var refSource = """
            public class EventData
            {
                public string? type;
                public int keycode;
            }
            """;
        var source = """
            public static class Game
            {
                public static string Describe(EventData e)
                {
                    return e.type + ":" + e.keycode;
                }
            }
            """;

        var result = Checker.Check([source], null, [refSource]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Empty(result.Warnings);
        var export = IlExport.Export([source], referenceSources: [refSource]);
        Assert.Empty(export.Diagnostics);
        Assert.True(export.Classes.Single(c => c.Name == "EventData").IsExternal);
    }
}
