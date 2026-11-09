namespace TinyCs.Tests;

public class SampleE2ETests
{
    [Fact]
    public void Sample_Hello_TranspilesAndRuns()
    {
        var result = RunSample("samples/hello.cs",
            "Hello.Greet(\"TinyC#\") + \",\" + Hello.Add(2, 3)");

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
    public void Sample_HostApiRef_ChecksAndCallsTheHost()
    {
        var source = File.ReadAllText(TestHelper.FindProjectFile(
            "samples/host_api_game.cs"));
        var refSource = File.ReadAllText(TestHelper.FindProjectFile(
            "samples/host_api_stub.cs"));
        var result = Checker.Check([source], null, [refSource]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Empty(result.Warnings);
        // stub の型は IL の class にせず、host の関数として呼ぶ
        var export = IlExport.Export([source], referenceSources: [refSource]);
        Assert.Empty(export.Diagnostics);
        Assert.All(export.Classes.Where(c => c.Name.StartsWith("HostApi_", StringComparison.Ordinal)), c => Assert.True(c.IsExternal, c.Name));
        var names = export.ForeignMethods.Select(m => m.Name).ToList();
        Assert.Contains("hostapi.screen.width", names);
        Assert.Contains("hostapi.time.delta_seconds", names);
        Assert.Contains("hostapi.log.info", names);
    }

    private static string RunSample(string relativePath, string expr)
    {
        var source = File.ReadAllText(TestHelper.FindProjectFile(relativePath));
        return TestHelper.CompileAndRunVm(source, expr);
    }
}
