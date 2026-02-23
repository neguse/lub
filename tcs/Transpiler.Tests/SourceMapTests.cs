namespace TinyCs.Tests;

public class SourceMapTests
{
    [Fact]
    public void SourceMap_MapsClassDeclaration()
    {
        var result = Transpiler.TranspileWithDiagnostics(
            ["public class Foo { }"], ["test.cs"]);
        Assert.True(result.Success);
        Assert.NotNull(result.SourceMap);
        Assert.True(result.SourceMap.Count > 0);
        // Find the "Foo = {}" line and check mapping
        var lua = result.Lua;
        var lines = lua.Split('\n');
        int fooLine = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("Foo = {}"))
            {
                fooLine = i + 1; // 1-based
                break;
            }
        }
        Assert.True(fooLine > 0, "Could not find 'Foo = {}' in output");
        var entry = result.SourceMap.Lookup(fooLine);
        Assert.NotNull(entry);
        Assert.Equal("test.cs", entry.Value.File);
        Assert.Equal(1, entry.Value.Line);
    }

    [Fact]
    public void SourceMap_MapsMethodDeclaration()
    {
        var source = "public class Calc\n{\n    public static int Add(int a, int b)\n    {\n        return a + b;\n    }\n}";
        var result = Transpiler.TranspileWithDiagnostics([source], ["calc.cs"]);
        Assert.True(result.Success);

        // Find the "function Calc.Add" line and check it maps to C# line 3
        var lua = result.Lua;
        var lines = lua.Split('\n');
        int funcLine = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains("function Calc.Add"))
            {
                funcLine = i + 1; // 1-based
                break;
            }
        }
        Assert.True(funcLine > 0, "Could not find function Calc.Add in output");
        var entry = result.SourceMap!.Lookup(funcLine);
        Assert.NotNull(entry);
        Assert.Equal("calc.cs", entry.Value.File);
        Assert.Equal(3, entry.Value.Line);
    }

    [Fact]
    public void SourceMap_MapsStatements()
    {
        var source = "public class App\n{\n    public static void Main()\n    {\n        int x = 10;\n        int y = 20;\n    }\n}";
        var result = Transpiler.TranspileWithDiagnostics([source], ["app.cs"]);
        Assert.True(result.Success);
        // There should be at least mappings for the two local declarations
        Assert.True(result.SourceMap!.Count >= 3); // class + method + statements
    }

    [Fact]
    public void SourceMap_MultiFile()
    {
        var result = Transpiler.TranspileWithDiagnostics(
            ["public class A { }", "public class B { }"],
            ["a.cs", "b.cs"]);
        Assert.True(result.Success);

        // Should have mappings to both files
        var json = result.SourceMap!.ToJson();
        Assert.Contains("a.cs", json);
        Assert.Contains("b.cs", json);
    }

    [Fact]
    public void SourceMap_ToJsonFormat()
    {
        var map = new SourceMap();
        map.Add(1, "test.cs", 5);
        map.Add(3, "test.cs", 10);
        var json = map.ToJson();
        Assert.Contains("\"version\": 1", json);
        Assert.Contains("\"1\":", json);
        Assert.Contains("\"line\": 5", json);
    }
}
