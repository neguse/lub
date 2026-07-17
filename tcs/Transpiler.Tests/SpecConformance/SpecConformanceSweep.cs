using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TinyCs.Tests.SpecConformance;

internal sealed class SpecConformanceSweep
{
    private const string UpdateBaselineVariable =
        "TCS_SPEC_UPDATE_BASELINE";
    private const string ReportVariable = "TCS_SPEC_REPORT";
    private const string AnnotationMarker = "<!-- Example: {";

    private static readonly JsonSerializerOptions BaselineJsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _repoRoot;
    private readonly string _standardDirectory;
    private readonly string _templateDirectory;
    private readonly string _baselinePath;

    public SpecConformanceSweep(string repoRoot)
    {
        _repoRoot = repoRoot;
        _standardDirectory = Path.Combine(repoRoot, "deps", "csharpstandard",
            "standard");
        _templateDirectory = Path.Combine(repoRoot, "deps", "csharpstandard",
            "tools", "example-templates");
        _baselinePath = Path.Combine(repoRoot, "Transpiler.Tests",
            "SpecConformance", "conformance-baseline.json");
    }

    public SpecSweepResult Run()
    {
        var extractor = new SpecExampleExtractor();
        var examples = extractor.ExtractDirectory(_standardDirectory);
        var expander = new SpecTemplateExpander(_templateDirectory);
        var classifier = new SpecConformanceClassifier();
        var classified = new List<ClassifiedSpecExample>(examples.Count);

        foreach (var example in examples)
        {
            var expansion = expander.Expand(example);
            classified.Add(new ClassifiedSpecExample(example,
                classifier.Classify(example, expansion)));
        }

        var reportPath = Environment.GetEnvironmentVariable(ReportVariable);
        if (!string.IsNullOrWhiteSpace(reportPath))
        {
            var resolved = Path.IsPathRooted(reportPath)
                ? reportPath
                : Path.Combine(_repoRoot, reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(resolved)!);
            File.WriteAllText(resolved, CreateReport(classified));
        }

        string? difference;
        if (Environment.GetEnvironmentVariable(UpdateBaselineVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_baselinePath)!);
            File.WriteAllText(_baselinePath,
                CreateBaselineJson(classified));
            difference = null;
        }
        else if (!File.Exists(_baselinePath))
        {
            difference = $"baseline not found: {_baselinePath}";
        }
        else
        {
            difference = CompareBaseline(File.ReadAllText(_baselinePath),
                classified);
        }

        return new SpecSweepResult(examples, classified, difference);
    }

    public int CountAnnotationMarkers()
    {
        var count = 0;
        foreach (var path in Directory.EnumerateFiles(_standardDirectory,
                     "*.md"))
        {
            var text = File.ReadAllText(path);
            var offset = 0;
            while ((offset = text.IndexOf(AnnotationMarker, offset,
                       StringComparison.Ordinal)) >= 0)
            {
                count++;
                offset += AnnotationMarker.Length;
            }
        }
        return count;
    }

    public static string FindRepoRoot()
    {
        string?[] starts =
        [
            AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(SpecConformanceSweep).Assembly.Location),
            Environment.CurrentDirectory
        ];

        foreach (var start in starts)
        {
            if (string.IsNullOrEmpty(start))
                continue;
            var directory = start;
            while (!string.IsNullOrEmpty(directory))
            {
                if (File.Exists(Path.Combine(directory, "tcs.slnx")))
                    return directory;
                var parent = Path.GetDirectoryName(directory);
                if (parent == directory)
                    break;
                directory = parent;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find repository root containing tcs.slnx.");
    }

    internal static string CreateBaselineJson(
        IEnumerable<ClassifiedSpecExample> classified)
    {
        var entries = CreateBaselineEntries(classified);
        return JsonSerializer.Serialize(entries, BaselineJsonOptions) + "\n";
    }

    internal static string CreateReport(
        IEnumerable<ClassifiedSpecExample> classified)
    {
        var items = ResolveIds(classified)
            .OrderBy(pair => pair.Item.Example.MdFile, StringComparer.Ordinal)
            .ThenBy(pair => pair.Id, StringComparer.Ordinal)
            .ToList();
        var categories = Enum.GetValues<SpecClassification>();
        var builder = new StringBuilder();
        builder.AppendLine(
            "<!-- Generated by run-spec-conformance.sh — do not edit -->");
        builder.AppendLine("# Specification conformance C0 report");
        builder.AppendLine();
        builder.AppendLine(
            "Source corpus: dotnet/csharpstandard (CC-BY-4.0), read at test time.");
        builder.AppendLine();
        builder.Append("| Markdown file | ");
        builder.Append(string.Join(" | ", categories));
        builder.AppendLine(" | Total |");
        builder.Append("|---|");
        foreach (var unused in categories)
            builder.Append("---:|");
        builder.AppendLine("---:|");

        foreach (var chapter in items.GroupBy(
                     pair => pair.Item.Example.MdFile, StringComparer.Ordinal))
        {
            builder.Append("| ").Append(chapter.Key).Append(" | ");
            builder.Append(string.Join(" | ", categories.Select(category =>
                chapter.Count(pair => pair.Item.Result.Category == category))));
            builder.Append(" | ").Append(chapter.Count()).AppendLine(" |");
        }

        builder.Append("| **Total** | ");
        builder.Append(string.Join(" | ", categories.Select(category =>
            items.Count(pair => pair.Item.Result.Category == category))));
        builder.Append(" | ").Append(items.Count).AppendLine(" |");
        builder.AppendLine();
        builder.AppendLine("## Unextracted reasons");
        builder.AppendLine();
        builder.AppendLine("| Reason | Count |");
        builder.AppendLine("|---|---:|");
        var reasons = items
            .Where(pair => pair.Item.Result.Category ==
                           SpecClassification.Unextracted)
            .GroupBy(pair => pair.Item.Result.Reason ?? "<missing>",
                StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToList();
        if (reasons.Count == 0)
            builder.AppendLine("| (none) | 0 |");
        else
            foreach (var reason in reasons)
                builder.Append("| ").Append(EscapeTable(reason.Key)).Append(" | ")
                    .Append(reason.Count()).AppendLine(" |");

        builder.AppendLine();
        builder.AppendLine("## Bugs");
        builder.AppendLine();
        var bugs = items.Where(pair =>
                pair.Item.Result.Category == SpecClassification.Bug)
            .ToList();
        if (bugs.Count == 0)
            builder.AppendLine("None.");
        else
            foreach (var (bug, id) in bugs)
                builder.Append("- `").Append(id).Append("`: ")
                    .AppendLine(OneLine(bug.Result.Details ?? "<no details>"));

        return builder.ToString();
    }

    internal static string? CompareBaseline(string json,
        IEnumerable<ClassifiedSpecExample> classified)
    {
        SortedDictionary<string, BaselineEntry>? expected;
        try
        {
            expected = JsonSerializer.Deserialize<
                SortedDictionary<string, BaselineEntry>>(json,
                BaselineJsonOptions);
        }
        catch (JsonException exception)
        {
            return $"baseline could not be parsed: {exception.Message}";
        }

        if (expected is null)
            return "baseline could not be parsed: root is null";
        var actual = CreateBaselineEntries(classified);
        var missing = expected.Keys.Except(actual.Keys, StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal).ToList();
        var extra = actual.Keys.Except(expected.Keys, StringComparer.Ordinal)
            .OrderBy(key => key, StringComparer.Ordinal).ToList();
        var changed = actual.Keys.Intersect(expected.Keys,
                StringComparer.Ordinal)
            .Where(key => actual[key] != expected[key])
            .OrderBy(key => key, StringComparer.Ordinal).ToList();
        if (missing.Count == 0 && extra.Count == 0 && changed.Count == 0)
            return null;

        var builder = new StringBuilder("conformance baseline mismatch");
        AppendKeys(builder, "missing from current sweep", missing);
        AppendKeys(builder, "extra in current sweep", extra);
        if (changed.Count > 0)
        {
            builder.AppendLine().AppendLine("changed:");
            foreach (var key in changed)
                builder.Append("  - ").Append(key).Append(": ")
                    .Append(Describe(expected[key])).Append(" -> ")
                    .AppendLine(Describe(actual[key]));
        }
        return builder.ToString().TrimEnd();
    }

    // 同一 md ファイル内で name が重複する例 (upstream の実在ケース) は、重複する
    // 全出現へ :L<行> を付与して衝突を解消する。片側だけの付与は文書順に依存する
    // ため採らない。
    internal static IReadOnlyList<(ClassifiedSpecExample Item, string Id)>
        ResolveIds(IEnumerable<ClassifiedSpecExample> classified)
    {
        var items = classified.ToList();
        var duplicates = items
            .GroupBy(item => item.Example.Id, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);
        return items.Select(item => (item, duplicates.Contains(item.Example.Id)
            ? $"{item.Example.Id}:L{item.Example.SourceLine}"
            : item.Example.Id)).ToList();
    }

    private static SortedDictionary<string, BaselineEntry>
        CreateBaselineEntries(IEnumerable<ClassifiedSpecExample> classified)
    {
        var entries = new SortedDictionary<string, BaselineEntry>(
            StringComparer.Ordinal);
        foreach (var (item, id) in ResolveIds(classified))
        {
            var entry = new BaselineEntry(item.Result.Category.ToString(),
                item.Result.Category == SpecClassification.Unextracted
                    ? item.Result.Reason
                    : null);
            if (!entries.TryAdd(id, entry))
                throw new InvalidOperationException(
                    $"Duplicate specification example id: {id}");
        }
        return entries;
    }

    private static void AppendKeys(StringBuilder builder, string heading,
        IReadOnlyCollection<string> keys)
    {
        if (keys.Count == 0)
            return;
        builder.AppendLine().Append(heading).AppendLine(":");
        foreach (var key in keys)
            builder.Append("  - ").AppendLine(key);
    }

    private static string Describe(BaselineEntry entry) => entry.Reason is null
        ? entry.Category
        : $"{entry.Category} ({entry.Reason})";

    private static string EscapeTable(string text) =>
        text.Replace("|", "\\|", StringComparison.Ordinal);

    private static string OneLine(string text) =>
        text.Replace("\r\n", "<br>", StringComparison.Ordinal)
            .Replace("\n", "<br>", StringComparison.Ordinal)
            .Replace('\r', ' ');

    private sealed record BaselineEntry(
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("reason")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Reason = null);
}
