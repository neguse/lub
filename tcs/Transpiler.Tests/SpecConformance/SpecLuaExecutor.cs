using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace TinyCs.Tests.SpecConformance;

internal sealed record SpecExecutionOutcome(bool Executed, bool Passed,
    bool KnownDifference, string? Details);

/// <summary>
/// InRun 分類の例のうち expectedOutput / ignoreOutput 付きを Lua 5.5 で実行し、
/// 仕様が明記する出力と照合する (C1)。inferOutput の dotnet オラクル照合は C2。
/// </summary>
internal sealed class SpecLuaExecutor
{
    private readonly string _runtimePath;
    private readonly IReadOnlyDictionary<string, string> _knownDifferences;
    private readonly SpecDotnetExecutor _dotnet = new();

    public SpecLuaExecutor(string repoRoot)
    {
        _runtimePath = Path.Combine(repoRoot, "runtime", "tinysystem.lua")
            .Replace("\\", "/");
        _knownDifferences = LoadKnownDifferences(Path.Combine(repoRoot,
            "Transpiler.Tests", "SpecConformance", "known-differences.json"));
    }

    // 出力契約 (expectedOutput / ignoreOutput) が無い InRun 例は dotnet
    // differential (C2) がオラクルになるため、全 InRun が実行対象。
    public static bool IsEligible(ClassifiedSpecExample item) =>
        item.Result.Category == SpecClassification.InRun &&
        item.Result.Lua is not null;

    public SpecExecutionOutcome Execute(ClassifiedSpecExample item, string id,
        IReadOnlyList<SpecSourceFile> sources)
    {
        string[]? expectedLines = null;
        if (item.Example.Annotation.ExpectedOutput is { } specOutput)
        {
            expectedLines = specOutput.Select(NormalizeExpectedLine).ToArray();
        }
        else if (!item.Example.Annotation.IgnoreOutput)
        {
            var dotnetRun = _dotnet.Run(sources,
                "Spec_" + id.Replace(':', '_').Replace('.', '_'));
            if (!dotnetRun.Ok)
            {
                if (_knownDifferences.TryGetValue(id, out var knownReason))
                    return new SpecExecutionOutcome(true, true, true,
                        knownReason);
                return new SpecExecutionOutcome(true, false, false,
                    $"dotnet oracle failed: {dotnetRun.Error}");
            }
            expectedLines = SplitOutput(dotnetRun.Output)
                .Select(NormalizeExpectedLine).ToArray();
        }

        var entry = FindEntryInvocation(sources);
        var script = $"local TinySystem = dofile(\"{_runtimePath}\")\n" +
                     "List = TinySystem.List\n" +
                     "Dict = TinySystem.Dict\n" +
                     "Math = TinySystem.Math\n" +
                     "String = TinySystem.String\n" +
                     "Random = TinySystem.Random\n" +
                     $"{item.Result.Lua}\n{entry}";

        string stdout;
        try
        {
            stdout = TestHelper.RunLua(script);
        }
        catch (Exception exception)
        {
            if (_knownDifferences.TryGetValue(id, out _))
                return new SpecExecutionOutcome(true, true, true,
                    exception.Message);
            return new SpecExecutionOutcome(true, false, false,
                $"Lua execution failed: {exception.Message}");
        }

        if (expectedLines is null)
            return new SpecExecutionOutcome(true, true, false, null);

        var actualLines = SplitOutput(stdout);
        if (actualLines.SequenceEqual(expectedLines, StringComparer.Ordinal))
            return new SpecExecutionOutcome(true, true, false, null);

        if (_knownDifferences.TryGetValue(id, out var reason))
            return new SpecExecutionOutcome(true, true, true, reason);

        return new SpecExecutionOutcome(true, false, false,
            "output mismatch\n" +
            $"expected: {string.Join(" | ", expectedLines)}\n" +
            $"actual:   {string.Join(" | ", actualLines)}");
    }

    // 正規化は全例共通の書式差のみ (design doc §5)。C# の bool 表記 True/False は
    // Lua では true/false、浮動小数の指数表記 E は Lua では e になる。
    // 意味論差は known-differences.json で例別に扱う。
    internal static string NormalizeExpectedLine(string line)
    {
        var normalized = System.Text.RegularExpressions.Regex.Replace(line,
            @"\b(True|False)\b",
            match => match.Value == "True" ? "true" : "false");
        return System.Text.RegularExpressions.Regex.Replace(normalized,
            @"(\d)E([+-]?\d)", "$1e$2");
    }

    private static string[] SplitOutput(string stdout)
    {
        var lines = stdout.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n').Select(line => line.TrimEnd()).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        return [.. lines];
    }

    // 実行の起点: top-level statements はチャンク実行だけで済む。それ以外は
    // static Main を持つ型を探し、namespace 込みの emitted 名で明示的に呼ぶ
    // (tcs は Main を自動実行しない — q.md Q3)。
    internal static string FindEntryInvocation(
        IReadOnlyList<SpecSourceFile> sources)
    {
        foreach (var source in sources)
        {
            var root = CSharpSyntaxTree.ParseText(source.Code).GetRoot();
            if (root.DescendantNodes().OfType<GlobalStatementSyntax>().Any())
                return "";

            var main = root.DescendantNodes()
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(method =>
                    method.Identifier.ValueText == "Main" &&
                    method.Modifiers.Any(SyntaxKind.StaticKeyword));
            if (main is null)
                continue;

            var parts = new List<string>();
            var node = main.Parent;
            while (node is not null)
            {
                if (node is TypeDeclarationSyntax type)
                    parts.Insert(0, type.Identifier.ValueText);
                else if (node is BaseNamespaceDeclarationSyntax ns)
                    parts.Insert(0, ns.Name.ToString());
                node = node.Parent;
            }
            return string.Join(".", parts) + ".Main()";
        }
        return "";
    }

    private static IReadOnlyDictionary<string, string> LoadKnownDifferences(
        string path)
    {
        if (!File.Exists(path))
            return new Dictionary<string, string>(StringComparer.Ordinal);
        var entries = JsonSerializer.Deserialize<
            Dictionary<string, KnownDifference>>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? [];
        return entries.ToDictionary(pair => pair.Key,
            pair => pair.Value.Reason, StringComparer.Ordinal);
    }

    private sealed record KnownDifference(string Reason);
}
