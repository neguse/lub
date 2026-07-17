using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;

namespace TinyCs.Tests.SpecConformance;

internal sealed partial class SpecConformanceClassifier
{
    // 分類は analyzer と同じ視点で行う: フル BCL 参照でコンパイルし、サブセット外は
    // compile error でなく TCS1001/1002/1003 診断として観測する。transpiler 既定の
    // 最小参照だと template の using (System.IO 等) 自体が CS0234 になり、
    // サブセット外 API の例がすべて分類不能になる。
    private static readonly Lazy<MetadataReference[]> FullReferences = new(() =>
        (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Append(typeof(global::TinySystem.Random).Assembly.Location)
        .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
        .Select(group => (MetadataReference)MetadataReference.CreateFromFile(
            group.First()))
        .ToArray());

    // 公式 template の csproj は ImplicitUsings=enable であり、"-without-using"
    // template の例は SDK の暗黙 global using を前提にしている。compliance 解析と
    // emit の対象外になるよう referenceSources として補う。
    private const string ImplicitUsingsSource = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    private readonly Func<string[], TranspileResult> _transpile;

    public SpecConformanceClassifier() : this(sources =>
        Transpiler.TranspileWithDiagnostics(sources, checkNaming: false,
            referenceSources: [ImplicitUsingsSource],
            references: FullReferences.Value))
    {
    }

    internal SpecConformanceClassifier(Func<string[], TranspileResult> transpile)
        => _transpile = transpile;

    public SpecClassificationResult Classify(SpecExample example,
        SpecExpansion expansion)
    {
        if (expansion.UnextractedReason is not null)
            return new SpecClassificationResult(SpecClassification.Unextracted,
                expansion.UnextractedReason, expansion.Details);

        try
        {
            var sources = expansion.Sources.Select(source => source.Code)
                .ToArray();
            var result = _transpile(sources);
            var expectedErrors = example.Annotation.ExpectedErrors;

            if (result.Errors.Count > 0)
            {
                if (expectedErrors is null)
                    return Unextracted("unexpected-compile-error",
                        result.Errors);
                if (!ContainsAllExpectedErrors(result.Errors, expectedErrors,
                        out var missing))
                    return Unextracted("expected-error-mismatch",
                        result.Errors, $"missing: {string.Join(", ", missing)}");
                return new SpecClassificationResult(SpecClassification.CsErr,
                    Details: string.Join("\n", result.Errors));
            }

            if (expectedErrors is not null)
                return Unextracted("expected-error-not-raised", []);

            if (result.Warnings.Any(warning =>
                    TinyCsDiagnosticRegex().IsMatch(warning)))
                return new SpecClassificationResult(SpecClassification.Diag,
                    Details: string.Join("\n", result.Warnings));

            if (string.IsNullOrEmpty(result.Lua))
                return new SpecClassificationResult(SpecClassification.Bug,
                    Details: "TranspileWithDiagnostics returned empty Lua.");

            return new SpecClassificationResult(expansion.IsExecutable
                ? SpecClassification.InRun
                : SpecClassification.InCompile);
        }
        catch (Exception exception)
        {
            return new SpecClassificationResult(SpecClassification.Bug,
                Details: $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    private static SpecClassificationResult Unextracted(string reason,
        IReadOnlyCollection<string> errors, string? prefix = null)
    {
        var details = string.Join("\n", errors);
        if (prefix is not null)
            details = details.Length == 0 ? prefix : $"{prefix}\n{details}";
        return new SpecClassificationResult(SpecClassification.Unextracted,
            reason, details.Length == 0 ? null : details);
    }

    private static bool ContainsAllExpectedErrors(
        IEnumerable<string> errors, IEnumerable<string> expectedErrors,
        out List<string> missing)
    {
        var actualCounts = errors
            .SelectMany(error => CompilerErrorRegex().Matches(error)
                .Select(match => match.Value))
            .GroupBy(code => code, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(),
                StringComparer.Ordinal);
        missing = [];
        foreach (var expected in expectedErrors)
        {
            if (actualCounts.TryGetValue(expected, out var count) && count > 0)
            {
                actualCounts[expected] = count - 1;
                continue;
            }
            missing.Add(expected);
        }
        return missing.Count == 0;
    }

    [GeneratedRegex(@"\bCS\d{4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex CompilerErrorRegex();

    [GeneratedRegex(@"\bTCS100[123]\b", RegexOptions.CultureInvariant)]
    private static partial Regex TinyCsDiagnosticRegex();
}
