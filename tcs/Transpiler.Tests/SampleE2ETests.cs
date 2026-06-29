namespace TinyCs.Tests;

public class SampleE2ETests
{
    [Fact]
    public void Sample_Hello_TranspilesAndRuns()
    {
        var result = RunSample("samples/hello.cs",
            """Hello.Greet("TinyC#") .. "," .. tostring(Hello.Add(2, 3))""");

        Assert.Equal("Hello, TinyC#!,5", result);
    }

    [Fact]
    public void Sample_Game_TranspilesAndRuns()
    {
        var result = RunSample("samples/game.cs", "Battle.Run()");

        Assert.Equal("Dragon: HP=145 [attacking] | alive=2", result);
    }

    [Fact]
    public void Sample_Inventory_TranspilesAndRuns()
    {
        var result = RunSample("samples/inventory.cs", "Game.Test()");

        Assert.Equal("Items=4 Total=330 Best=Sword Shield=1", result);
    }

    [Fact]
    public void Sample_Entity_TranspilesAndRuns()
    {
        var result = RunSample("samples/entity.cs", "EntitySample.Run()");

        Assert.Equal("Slime:enemy@6,2 HP=13", result);
    }

    [Fact]
    public void Sample_StateMachine_TranspilesAndRuns()
    {
        var result = RunSample("samples/statemachine.cs", "StateMachineSample.Run()");

        Assert.Equal("open,open,locked,closed", result);
    }

    [Fact]
    public void Sample_Collision_TranspilesAndRuns()
    {
        var result = RunSample("samples/collision.cs", "CollisionSample.Run()");

        Assert.Equal("hit,miss,hit", result);
    }

    [Fact]
    public void Sample_HostApiRef_TranspilesAndRuns()
    {
        var source = File.ReadAllText(TestHelper.FindProjectFile(
            "samples/host_api_game.cs"));
        var refSource = File.ReadAllText(TestHelper.FindProjectFile(
            "samples/host_api_stub.cs"));
        var result = Transpiler.TranspileWithDiagnostics(
            [source], null, [refSource]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.DoesNotContain("Screen = {}", result.Lua);
        Assert.DoesNotContain("Time = {}", result.Lua);
        Assert.DoesNotContain("Log = {}", result.Lua);

        var runtimePath = TestHelper.FindProjectFile("runtime/tinysystem.lua");
        var script = $$"""
            local TinySystem = dofile("{{runtimePath}}")
            String = TinySystem.String
            LastLog = ""
            Screen = {
              Width = function() return 1280 end,
              Height = function() return 720 end
            }
            Time = {
              DeltaSeconds = function() return 0.16 end
            }
            Log = {
              Info = function(message) LastLog = message end
            }
            {{result.Lua}}
            print(HostApiSample.DescribeFrame())
            print(LastLog)
            """;
        var output = TestHelper.RunLua(script).Trim()
            .Split('\n', StringSplitOptions.TrimEntries);

        Assert.Equal("screen=1280x720 dt=0.16", output[0]);
        Assert.Equal("describe-frame", output[1]);
    }

    private static string RunSample(string relativePath, string luaExpr)
    {
        var source = File.ReadAllText(TestHelper.FindProjectFile(relativePath));
        return TestHelper.TranspileAndRunWithRuntime(source, luaExpr);
    }
}
