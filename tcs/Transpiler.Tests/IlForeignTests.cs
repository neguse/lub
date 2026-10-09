namespace TinyCs.Tests;

public class IlForeignTests
{
    // C backend へ渡す foreign 関数名も Lua と同じ規則 (namespace を含めた
    // 小文字パス)
    [Fact]
    public void NamespacedReferenceStubs_ExportNamespaceQualifiedForeignNames()
    {
        var program = IlExport.Export(["""
            using Lub;
            public class Game { public static void Main() { App.Quit(); Gfx.Clear(1); } }
            """], referenceSources: ["""
            namespace Lub
            {
                public static class App { public static void Quit() { } }
                public static class Gfx { public static void Clear(int c) { } }
            }
            """]);
        Assert.Empty(program.Diagnostics);
        Assert.Equal(["lub.app.quit", "lub.gfx.clear"],
            program.ForeignMethods.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void ReferenceStubsSupplyTypesAndCallsWithoutCompilingStubBodies()
    {
        var program = IlExport.Export(["""
            public class Game {
                public static void Main() {
                    Engine.Poll(out string topic);
                    Engine.Send(new Options { Version = 2 });
                }
            }
            """], referenceSources: ["""
            public class Options { public int? Version; }
            public static class Engine {
                public static void Send(Options options) { throw new System.Exception(); }
                public static void Poll(out string topic) { throw new System.Exception(); }
            }
            """]);
        Assert.Empty(program.Diagnostics);
        Assert.DoesNotContain(program.Classes, c => c.Name == "Engine");
        Assert.True(program.Classes.Single(c => c.Name == "Options").IsExternal);
        Assert.Equal(2, program.ForeignMethods.Length);
        Assert.True(program.ForeignMethods.Single(m => m.Name == "engine.poll").Parameters[0].IsOut);
        var local = Assert.IsType<IlLocal>(program.Classes.Single(c => c.Name == "Game").Methods[0].Body!.Stats[0]);
        Assert.Equal("string", local.Type);
    }

    [Fact]
    public void ReferenceStubInstanceMethodsExportWithReceiver()
    {
        var program = IlExport.Export(["""
            public class Game {
                public static int F() { Blob b = Host.Load(); return b.Get(0); }
            }
            """], referenceSources: ["""
            public class Blob { public int Length; public int Get(int index) { return 0; } }
            public static class Host { public static Blob Load() { return null!; } }
            """]);
        Assert.Empty(program.Diagnostics);
        var get = program.ForeignMethods.Single(m => m.Receiver == "Blob");
        Assert.EndsWith(".get", get.Name);
        Assert.Equal("int", get.ReturnType);
        Assert.Null(program.ForeignMethods.Single(m => m.Name.EndsWith(".load")).Receiver);
    }

    // Receiver は C backend が class 表を引く名前なので、namespace の中の型でも
    // 外部 class の IL 名 (namespace 修飾名) と一致させる
    [Fact]
    public void NamespacedReferenceStubInstanceMethods_ReceiverMatchesExternalClass()
    {
        var program = IlExport.Export(["""
            using Lub;
            public class Game {
                public static int F() { Blob b = Host.Load(); return b.Get(0); }
            }
            """], referenceSources: ["""
            namespace Lub
            {
                public class Blob { public int Get(int index) { return 0; } }
                public static class Host { public static Blob Load() { return null!; } }
            }
            """]);
        Assert.Empty(program.Diagnostics);
        var get = program.ForeignMethods.Single(m => m.Name == "lub.blob.get");
        Assert.Equal("Lub_Blob", get.Receiver);
        Assert.True(program.Classes.Single(c => c.Name == "Lub_Blob").IsExternal);
    }
}
