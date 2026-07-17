using System.Globalization;

namespace TinyCs.Tests.SpecConformance;

internal sealed record FuzzOutcome(bool Ok, string Details)
{
    public static readonly FuzzOutcome Pass = new(true, "");
}

/// <summary>
/// 生成プログラムを tcs → Lua と実 .NET の両方で実行して突き合わせる (C4)。
/// 失敗時は文単位の greedy 縮小で最小再現を作る。
/// </summary>
internal sealed class FuzzRunner
{
    private readonly string _runtimePath;
    private readonly SpecDotnetExecutor _dotnet = new();

    public FuzzRunner(string repoRoot) =>
        _runtimePath = Path.Combine(repoRoot, "runtime", "tinysystem.lua")
            .Replace("\\", "/");

    /// <param name="mutateLua">検出網の自己検証用: 生成 Lua への故障注入</param>
    public FuzzOutcome RunOne(string source,
        Func<string, string>? mutateLua = null)
    {
        var result = Transpiler.TranspileWithDiagnostics([source],
            checkNaming: false);
        if (result.Errors.Count > 0)
            return new FuzzOutcome(false,
                "generator produced invalid C#:\n"
                + string.Join("\n", result.Errors));
        if (result.Warnings.Any(w => w.Contains("TCS100")))
            return new FuzzOutcome(false,
                "generator produced diagnosed subset violation:\n"
                + string.Join("\n", result.Warnings));

        var lua = mutateLua is null ? result.Lua : mutateLua(result.Lua);
        var script = $"local TinySystem = dofile(\"{_runtimePath}\")\n" +
                     "List = TinySystem.List\n" +
                     "Dict = TinySystem.Dict\n" +
                     "Math = TinySystem.Math\n" +
                     "String = TinySystem.String\n" +
                     "Random = TinySystem.Random\n" +
                     $"{lua}\nProgram.Main()";
        string luaOut;
        try
        {
            luaOut = TestHelper.RunLua(script);
        }
        catch (Exception e)
        {
            return new FuzzOutcome(false, $"lua execution failed: {e.Message}");
        }

        var dotnetRun = _dotnet.Run(
            [new SpecSourceFile("Program.cs", source)], "TcsFuzz");
        if (!dotnetRun.Ok)
            return new FuzzOutcome(false,
                $"dotnet execution failed: {dotnetRun.Error}");

        var luaLines = SplitLines(luaOut);
        var dotnetLines = SplitLines(dotnetRun.Output)
            .Select(SpecLuaExecutor.NormalizeExpectedLine).ToArray();
        if (luaLines.Length != dotnetLines.Length)
            return Mismatch(dotnetLines, luaLines);
        for (var index = 0; index < luaLines.Length; index++)
            if (!LinesAgree(dotnetLines[index], luaLines[index]))
                return Mismatch(dotnetLines, luaLines);
        return FuzzOutcome.Pass;
    }

    /// <summary>
    /// 失敗した seed の文リストを greedy に削って最小再現ソースを返す。
    /// </summary>
    public string Reduce(IReadOnlyList<string> statements,
        Func<string, string>? mutateLua = null)
    {
        var current = statements.ToList();
        var shrunk = true;
        while (shrunk && current.Count > 1)
        {
            shrunk = false;
            for (var index = current.Count - 1; index >= 0; index--)
            {
                var candidate = current.Where((_, i) => i != index).ToList();
                var outcome = RunOne(FuzzGenerator.Assemble(candidate),
                    mutateLua);
                if (!outcome.Ok)
                {
                    current = candidate;
                    shrunk = true;
                }
            }
        }
        return FuzzGenerator.Assemble(current);
    }

    private static FuzzOutcome Mismatch(string[] dotnetLines,
        string[] luaLines) => new(false,
        "output mismatch\n" +
        $"dotnet: {string.Join(" | ", dotnetLines)}\n" +
        $"lua:    {string.Join(" | ", luaLines)}");

    private static bool LinesAgree(string dotnetLine, string luaLine)
    {
        if (dotnetLine == luaLine)
            return true;
        return double.TryParse(dotnetLine, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var a)
            && double.TryParse(luaLine, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var b)
            && a.Equals(b);
    }

    private static string[] SplitLines(string output)
    {
        var lines = output.ReplaceLineEndings("\n").Split('\n')
            .Select(line => line.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return [.. lines];
    }
}
