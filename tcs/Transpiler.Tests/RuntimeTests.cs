namespace TinyCs.Tests;

/// <summary>
/// Tests for the Lua runtime library (runtime/tinysystem.lua).
/// These test the runtime directly via Lua, not through the transpiler.
/// </summary>
public class RuntimeTests
{
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
    public void List_Where()
    {
        var result = RunWithRuntime("""
            local l = {1, 2, 3, 4, 5, 6}
            local evens = List.Where(l, function(x) return x % 2 == 0 end)
            print(#evens .. ":" .. evens[1] .. "," .. evens[2] .. "," .. evens[3])
            """);
        Assert.Equal("3:2,4,6", result);
    }

    [Fact]
    public void List_Select()
    {
        var result = RunWithRuntime("""
            local l = {1, 2, 3}
            local doubled = List.Select(l, function(x) return x * 2 end)
            print(doubled[1] .. "," .. doubled[2] .. "," .. doubled[3])
            """);
        Assert.Equal("2,4,6", result);
    }

    [Fact]
    public void List_WhereSelect_Chain()
    {
        var result = RunWithRuntime("""
            local l = {1, 2, 3, 4, 5}
            local result = List.Select(List.Where(l, function(x) return x > 2 end), function(x) return x * 10 end)
            print(result[1] .. "," .. result[2] .. "," .. result[3])
            """);
        Assert.Equal("30,40,50", result);
    }

    [Fact]
    public void List_Any_All()
    {
        var result = RunWithRuntime("""
            local l = {2, 4, 6}
            local anyOdd = List.Any(l, function(x) return x % 2 ~= 0 end)
            local allEven = List.All(l, function(x) return x % 2 == 0 end)
            print(tostring(anyOdd) .. "," .. tostring(allEven))
            """);
        Assert.Equal("false,true", result);
    }

    [Fact]
    public void List_First()
    {
        var result = RunWithRuntime("""
            local l = {10, 20, 30}
            print(List.First(l))
            """);
        Assert.Equal("10", result);
    }

    [Fact]
    public void List_OrderBy()
    {
        var result = RunWithRuntime("""
            local l = {3, 1, 4, 1, 5}
            local sorted = List.OrderBy(l, function(x) return x end)
            print(sorted[1] .. "," .. sorted[2] .. "," .. sorted[3] .. "," .. sorted[4] .. "," .. sorted[5])
            """);
        Assert.Equal("1,1,3,4,5", result);
    }

    [Fact]
    public void List_MinMaxSum()
    {
        var result = RunWithRuntime("""
            local l = {3, 1, 4, 1, 5}
            print(List.Min(l) .. "," .. List.Max(l) .. "," .. List.Sum(l))
            """);
        Assert.Equal("1,5,14", result);
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
