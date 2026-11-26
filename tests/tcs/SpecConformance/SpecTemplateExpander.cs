namespace TinyCs.Tests.SpecConformance;

internal sealed class SpecTemplateExpander
{
    private const string ExampleCodePlaceholder = "$example-code";
    private const string InlineFilePrefix = "// File ";

    private static readonly HashSet<string> ExecutableTemplates =
        new(StringComparer.Ordinal)
        {
            "standalone-console",
            "standalone-console-without-using",
            "code-in-main",
            "code-in-main-without-using",
            "code-in-partial-class"
        };

    private static readonly HashSet<string> LibraryTemplates =
        new(StringComparer.Ordinal)
        {
            "standalone-lib",
            "standalone-lib-without-using",
            "code-in-class-lib",
            "code-in-class-lib-without-using"
        };

    private readonly string _templateRoot;

    public SpecTemplateExpander(string templateRoot) =>
        _templateRoot = templateRoot;

    public SpecExpansion Expand(SpecExample example)
    {
        if (example.ExtractionFailureReason is not null)
            return SpecExpansion.Unextracted(example.ExtractionFailureReason,
                example.ExtractionFailureDetails);

        var annotation = example.Annotation;
        var template = annotation.Template;
        if (template == "extern-lib")
            return SpecExpansion.Unextracted(
                "unsupported-template:extern-lib");
        if (annotation.Project is not null ||
            annotation.ExternAliasSupport is not null)
            return SpecExpansion.Unextracted(
                "unsupported-template:extern-alias-support");
        if (annotation.ExecutionArgs is not null)
            return SpecExpansion.Unextracted(
                "unsupported-template:execution-args");
        if (annotation.ExpectedException is not null)
            return SpecExpansion.Unextracted(
                "unsupported-template:expected-exception");

        var executable = template is not null &&
            ExecutableTemplates.Contains(template);
        if (!executable &&
            (template is null || !LibraryTemplates.Contains(template)))
            return SpecExpansion.Unextracted(
                $"unsupported-template:{template ?? "<missing>"}");

        var templateDirectory = Path.Combine(_templateRoot, template!);
        if (!Directory.Exists(templateDirectory))
            return SpecExpansion.Unextracted(
                $"unsupported-template:{template}",
                $"template directory not found: {templateDirectory}");

        try
        {
            var (primaryCode, inlineFiles) = SplitInlineFiles(example.Code);
            var sources = Directory.EnumerateFiles(templateDirectory, "*.cs")
                .OrderBy(path => Path.GetFileName(path), StringComparer.Ordinal)
                .Select(path => new SpecSourceFile(Path.GetFileName(path),
                    File.ReadAllText(path).Replace(ExampleCodePlaceholder,
                        primaryCode, StringComparison.Ordinal)))
                .ToList();

            sources.AddRange(inlineFiles);
            foreach (var additionalFile in annotation.AdditionalFiles ?? [])
            {
                if (Path.GetFileName(additionalFile) != additionalFile)
                    return MissingAdditionalFile(additionalFile,
                        "path must be a file name");
                var path = Path.Combine(_templateRoot, "additional-files",
                    additionalFile);
                if (!File.Exists(path))
                    return MissingAdditionalFile(additionalFile,
                        $"file not found: {path}");
                sources.Add(new SpecSourceFile(additionalFile,
                    File.ReadAllText(path)));
            }

            return new SpecExpansion(sources, executable);
        }
        catch (Exception exception) when (exception is IOException or
                                            UnauthorizedAccessException)
        {
            return SpecExpansion.Unextracted("template-expansion-error",
                exception.Message);
        }
    }

    private static SpecExpansion MissingAdditionalFile(string fileName,
        string details) => SpecExpansion.Unextracted(
            $"additional-file-not-found:{fileName}", details);

    private static (string PrimaryCode, List<SpecSourceFile> InlineFiles)
        SplitInlineFiles(string code)
    {
        var normalized = code.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var lines = normalized.Split('\n');
        var primaryLines = new List<string>();
        var currentLines = new List<string>();
        var inlineFiles = new List<SpecSourceFile>();
        string? currentFile = null;

        foreach (var line in lines)
        {
            if (!line.StartsWith(InlineFilePrefix, StringComparison.Ordinal))
            {
                (currentFile is null ? primaryLines : currentLines).Add(line);
                continue;
            }

            if (currentFile is not null)
                inlineFiles.Add(new SpecSourceFile(currentFile,
                    string.Join("\n", currentLines)));
            currentLines.Clear();
            currentFile = line[InlineFilePrefix.Length..].TrimEnd(':').Trim();
        }

        if (currentFile is not null)
            inlineFiles.Add(new SpecSourceFile(currentFile,
                string.Join("\n", currentLines)));

        return (string.Join("\n", primaryLines), inlineFiles);
    }
}
