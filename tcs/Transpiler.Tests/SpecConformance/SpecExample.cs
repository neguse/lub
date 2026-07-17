using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyCs.Tests.SpecConformance;

public enum SpecClassification
{
    InRun,
    InCompile,
    Diag,
    CsErr,
    Unextracted,
    Bug
}

internal sealed record SpecAnnotation
{
    [JsonPropertyName("template")]
    public string? Template { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("replaceEllipsis")]
    public bool ReplaceEllipsis { get; init; }

    [JsonPropertyName("customEllipsisReplacements")]
    public string?[]? CustomEllipsisReplacements { get; init; }

    [JsonPropertyName("expectedErrors")]
    public string[]? ExpectedErrors { get; init; }

    [JsonPropertyName("expectedWarnings")]
    public string[]? ExpectedWarnings { get; init; }

    [JsonPropertyName("ignoredWarnings")]
    public string[]? IgnoredWarnings { get; init; }

    [JsonPropertyName("expectedOutput")]
    public string[]? ExpectedOutput { get; init; }

    [JsonPropertyName("inferOutput")]
    public bool InferOutput { get; init; }

    [JsonPropertyName("ignoreOutput")]
    public bool IgnoreOutput { get; init; }

    [JsonPropertyName("expectedException")]
    public string? ExpectedException { get; init; }

    [JsonPropertyName("additionalFiles")]
    public string[]? AdditionalFiles { get; init; }

    [JsonPropertyName("project")]
    public string? Project { get; init; }

    [JsonPropertyName("externAliasSupport")]
    public string? ExternAliasSupport { get; init; }

    [JsonPropertyName("executionArgs")]
    public string[]? ExecutionArgs { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? OtherDirectives { get; init; }
}

internal sealed record SpecExample(
    string MdFile,
    SpecAnnotation Annotation,
    string Code,
    int SourceLine,
    string? ExtractionFailureReason = null,
    string? ExtractionFailureDetails = null)
{
    public string? Name => Annotation.Name;
    public string? Template => Annotation.Template;
    public string Id => $"{MdFile}:" +
        (string.IsNullOrEmpty(Name) ? $"L{SourceLine}" : Name);
}

internal sealed record SpecSourceFile(string FileName, string Code);

internal sealed record SpecExpansion(
    IReadOnlyList<SpecSourceFile> Sources,
    bool IsExecutable,
    string? UnextractedReason = null,
    string? Details = null)
{
    public static SpecExpansion Unextracted(string reason,
        string? details = null) => new([], false, reason, details);
}

internal sealed record SpecClassificationResult(
    SpecClassification Category,
    string? Reason = null,
    string? Details = null,
    string? Lua = null);

internal sealed record ClassifiedSpecExample(
    SpecExample Example,
    SpecClassificationResult Result);

internal sealed record SpecExecutionStats(
    int Executed,
    int Passed,
    int KnownDifferences);

internal sealed record SpecSweepResult(
    IReadOnlyList<SpecExample> Examples,
    IReadOnlyList<ClassifiedSpecExample> Classified,
    string? BaselineDifference,
    SpecExecutionStats Execution);
