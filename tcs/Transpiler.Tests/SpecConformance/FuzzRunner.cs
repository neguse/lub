namespace TinyCs.Tests.SpecConformance;

internal sealed record FuzzOutcome(bool Ok, string Details)
{
    public static readonly FuzzOutcome Pass = new(true, "");
}

/// <summary>
/// 生成プログラムを tcs VM (tcs2vm → tcsvm、通常と GC stress) と実 .NET の両方で実行し、stdout の行が一致することを要求する (C4)。
/// C backend は tcs2c.Tests の FuzzDifferentialTests が同じ生成器で見る。失敗時は文単位の greedy 縮小で最小再現を作る。
/// </summary>
internal sealed class FuzzRunner
{
    private readonly SpecDotnetExecutor _dotnet = new();

    /// <param name="mutateVm">検出網の自己検証用: VM に渡す C# への故障注入 (.NET には元の source を渡す)</param>
    public FuzzOutcome RunOne(string source,
        Func<string, string>? mutateVm = null)
    {
        var result = Transpiler.TranspileWithDiagnostics([source]);
        if (result.Errors.Count > 0)
            return new FuzzOutcome(false,
                "generator produced invalid C#:\n"
                + string.Join("\n", result.Errors));
        if (result.Warnings.Any(w => w.Contains("TCS100")))
            return new FuzzOutcome(false,
                "generator produced diagnosed subset violation:\n"
                + string.Join("\n", result.Warnings));

        string vmOut;
        try
        {
            vmOut = VmCorpus.RunProgram(mutateVm is null ? source : mutateVm(source), "Program");
        }
        catch (Exception e)
        {
            return new FuzzOutcome(false, $"vm execution failed: {e.Message}");
        }
        var dotnetRun = _dotnet.Run(
            [new SpecSourceFile("Program.cs", source)], "TcsFuzz");
        if (!dotnetRun.Ok)
            return new FuzzOutcome(false,
                $"dotnet execution failed: {dotnetRun.Error}");

        var vmLines = SplitLines(vmOut);
        var dotnetLines = SplitLines(dotnetRun.Output);
        return vmLines.SequenceEqual(dotnetLines, StringComparer.Ordinal)
            ? FuzzOutcome.Pass
            : Mismatch(dotnetLines, vmLines);
    }

    /// <summary>
    /// 失敗した seed の文 / helper / 型リストを greedy に削って
    /// 最小再現ソースを返す。
    /// </summary>
    public string Reduce(IReadOnlyList<string> statements,
        Func<string, string>? mutateVm = null,
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
                        currentHelpers, currentTypes), mutateVm))
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
                        candidate, currentTypes), mutateVm))
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
                        currentHelpers, candidate), mutateVm))
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
        Func<string, string>? mutateVm)
    {
        var outcome = RunOne(source, mutateVm);
        return !outcome.Ok && !outcome.Details.StartsWith(
            "generator produced invalid C#", StringComparison.Ordinal);
    }

    private static FuzzOutcome Mismatch(string[] dotnetLines,
        string[] vmLines) => new(false,
        "output mismatch\n" +
        $"dotnet: {string.Join(" | ", dotnetLines)}\n" +
        $"vm:     {string.Join(" | ", vmLines)}");

    private static string[] SplitLines(string output)
    {
        var lines = output.ReplaceLineEndings("\n").Split('\n')
            .Select(line => line.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return [.. lines];
    }
}
