namespace TinyCs.Tests;

public class IlForeignTests
{
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
}
