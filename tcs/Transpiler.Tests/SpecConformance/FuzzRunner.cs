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
    /// 失敗した seed の文 / helper / 型リストを greedy に削って
    /// 最小再現ソースを返す。
    /// </summary>
    public string Reduce(IReadOnlyList<string> statements,
        Func<string, string>? mutateLua = null,
        IReadOnlyList<string>? helpers = null,
        IReadOnlyList<string>? types = null)
    {
        var currentStatements = statements.ToList();
        var currentHelpers = (helpers ?? []).ToList();
        var currentTypes = (types ?? []).ToList();
        var shrunk = true;
        while (shrunk)
        {
            shrunk = false;
            for (var index = currentStatements.Count - 1;
                index >= 0 && currentStatements.Count > 1; index--)
            {
                var candidate = currentStatements
                    .Where((_, i) => i != index).ToList();
                if (StillFailsSameWay(FuzzGenerator.Assemble(candidate,
                        currentHelpers, currentTypes), mutateLua))
                {
                    currentStatements = candidate;
                    shrunk = true;
                }
            }
            for (var index = currentHelpers.Count - 1; index >= 0; index--)
            {
                var candidate = currentHelpers
                    .Where((_, i) => i != index).ToList();
                if (StillFailsSameWay(FuzzGenerator.Assemble(currentStatements,
                        candidate, currentTypes), mutateLua))
                {
                    currentHelpers = candidate;
                    shrunk = true;
                }
            }
            for (var index = currentTypes.Count - 1; index >= 0; index--)
            {
                var candidate = currentTypes
                    .Where((_, i) => i != index).ToList();
                if (StillFailsSameWay(FuzzGenerator.Assemble(currentStatements,
                        currentHelpers, candidate), mutateLua))
                {
                    currentTypes = candidate;
                    shrunk = true;
                }
            }
        }
        return FuzzGenerator.Assemble(currentStatements, currentHelpers,
            currentTypes);
    }

    // 使用中の変数/helper を消すと C# として不正になる。その候補を採用すると
    // semantic bug の再現が compile error へすり替わるため除外する
    private bool StillFailsSameWay(string source,
        Func<string, string>? mutateLua)
    {
        var outcome = RunOne(source, mutateLua);
        return !outcome.Ok && !outcome.Details.StartsWith(
            "generator produced invalid C#", StringComparison.Ordinal);
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
