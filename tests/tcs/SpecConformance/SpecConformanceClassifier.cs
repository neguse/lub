using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace TinyCs.Tests.SpecConformance;

internal sealed partial class SpecConformanceClassifier
{
    // Classification uses the same viewpoint as the analyzer: compile with full BCL references, and observe out-of-subset code
    // as TCS1001/1002/1003 diagnostics rather than compile errors. With the transpiler's default
    // minimal references, the template's usings (System.IO etc.) themselves become CS0234, and
    // every out-of-subset API example becomes unclassifiable.
    internal static readonly Lazy<MetadataReference[]> FullReferences = new(() =>
        (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        .Append(typeof(global::TinySystem.Random).Assembly.Location)
        .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
        .Select(group => (MetadataReference)MetadataReference.CreateFromFile(
            group.First()))
        .ToArray());

    // The official template csproj has ImplicitUsings=enable, and the "-without-using"
    // template examples assume the SDK's implicit global usings. To keep them out of compliance analysis and
    // emit, they are supplied as referenceSources.
    internal const string ImplicitUsingsSource = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Threading;
        global using System.Threading.Tasks;
        """;

    private readonly Func<string[], CheckResult> _check;

    public SpecConformanceClassifier() : this(CheckWithRoslynOracle)
    {
    }

    internal SpecConformanceClassifier(Func<string[], CheckResult> check)
        => _check = check;

    public SpecClassificationResult Classify(SpecExample example,
        SpecExpansion expansion) => ClassifyCore(example, expansion);

    // Roslyn is used only to judge whether the code is valid C# (the acceptance oracle); the checks are done by the tcs compiler (same as tcs check).
    // Examples Roslyn rejects are classified by Roslyn's error (CSxxxx); if tcs rejects an example Roslyn accepted with a C# error, it is a compiler bug
    private static CheckResult CheckWithRoslynOracle(string[] sources)
    {
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s))
            .Append(CSharpSyntaxTree.ParseText(ImplicitUsingsSource))
            .Concat(TcsLibrary.LibraryTrees.Where(t => t.FilePath != TcsLibrary.ValueTupleLibraryPath))
            .ToArray();
        var hasTopLevel = trees.Any(t => t.GetCompilationUnitRoot().Members
            .OfType<Microsoft.CodeAnalysis.CSharp.Syntax.GlobalStatementSyntax>().Any());
        var compilation = CSharpCompilation.Create("SpecOracle", trees, FullReferences.Value,
            new CSharpCompilationOptions(hasTopLevel ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary));
        var roslynErrors = compilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d =>
            {
                var span = d.Location.GetLineSpan();
                return $"{span.Path}({span.StartLinePosition.Line + 1},{span.StartLinePosition.Character + 1}): error {d.Id}: {d.GetMessage()}";
            })
            .ToList();
        if (roslynErrors.Count > 0)
            return new CheckResult { Errors = roslynErrors };
        var result = Checker.Check(sources, referenceSources: [ImplicitUsingsSource]);
        var csErrors = result.Errors.Where(e => CompilerErrorRegex().IsMatch(e) && !TinyCsDiagnosticRegex().IsMatch(e)).ToList();
        if (csErrors.Count > 0)
            throw new InvalidOperationException("tcs rejected C# that Roslyn accepts:\n" + string.Join("\n", csErrors));
        return result;
    }

    private SpecClassificationResult ClassifyCore(SpecExample example,
        SpecExpansion expansion)
    {
        if (expansion.UnextractedReason is not null)
            return new SpecClassificationResult(SpecClassification.Unextracted,
                expansion.UnextractedReason, expansion.Details);

        try
        {
            var sources = expansion.Sources.Select(source => source.Code)
                .ToArray();
            var result = _check(sources);
            var expectedErrors = example.Annotation.ExpectedErrors;

            if (result.Errors.Count > 0)
            {
                // A TinyC# diagnostic that stops the build (error TCS1001) is not a C# compile error
                // but an out-of-subset report, so it is counted as Diag
                if (expectedErrors is null
                    && result.Errors.All(error =>
                        TinyCsDiagnosticRegex().IsMatch(error)))
                    return new SpecClassificationResult(SpecClassification.Diag,
                        Details: string.Join("\n",
                            result.Errors.Concat(result.Warnings)));
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
