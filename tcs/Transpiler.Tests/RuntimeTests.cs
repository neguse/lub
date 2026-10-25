namespace TinyCs.Tests;

/// <summary>
/// Tests for the Lua runtime library (runtime/tinysystem.lua).
/// These test the runtime directly via Lua, not through the transpiler.
/// </summary>
public class RuntimeTests
{
    [Fact]
    public void LuaBinary_IsLua55()
    {
        Assert.StartsWith("Lua 5.5", TestHelper.LuaVersion);
    }

    private static string RunWithRuntime(string luaCode)
    {
        var runtimePath = TestHelper.FindProjectFile("runtime/tinysystem.lua");
        var script = $"local TinySystem = dofile(\"{runtimePath}\")\nlocal List = TinySystem.List\nlocal Dict = TinySystem.Dict\nlocal Math = TinySystem.Math\n{luaCode}";
        return TestHelper.RunLua(script).Trim();
    }

    [Fact]
    public void List_AddAndCount()
    {
        var result = RunWithRuntime("""
            local l = List.new()
            List.Add(l, 10)
            List.Add(l, 20)
            List.Add(l, 30)
            print(List.Count(l))
            """);
        Assert.Equal("3", result);
    }

    [Fact]
    public void List_Contains()
    {
        var result = RunWithRuntime("""
            local l = List.new({1, 2, 3})
            print(tostring(List.Contains(l, 2)) .. "," .. tostring(List.Contains(l, 5)))
            """);
        Assert.Equal("true,false", result);
    }

    [Fact]
    public void Dict_AddAndContainsKey()
    {
        var result = RunWithRuntime("""
            local d = Dict.new()
            Dict.Add(d, "a", 1)
            Dict.Add(d, "b", 2)
            print(tostring(Dict.ContainsKey(d, "a")) .. "," .. tostring(Dict.ContainsKey(d, "c")))
            """);
        Assert.Equal("true,false", result);
    }

    [Fact]
    public void Dict_Count()
    {
        var result = RunWithRuntime("""
            local d = {x = 1, y = 2, z = 3}
            print(Dict.Count(d))
            """);
        Assert.Equal("3", result);
    }

    [Fact]
    public void Math_Clamp()
    {
        var result = RunWithRuntime("""
            print(Math.Clamp(-5, 0, 10) .. "," .. Math.Clamp(15, 0, 10) .. "," .. Math.Clamp(5, 0, 10))
            """);
        Assert.Equal("0,10,5", result);
    }

    [Fact]
    public void Math_Sqrt()
    {
        var result = RunWithRuntime("""
            print(Math.Sqrt(25))
            """);
        Assert.Equal("5.0", result);
    }
}
