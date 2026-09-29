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
}
