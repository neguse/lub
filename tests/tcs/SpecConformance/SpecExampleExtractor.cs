using System.Text;
using System.Text.Json;

namespace TinyCs.Tests.SpecConformance;

internal sealed class SpecExampleExtractor
{
    private const string AnnotationPrefix = "<!-- Example:";
    private const string CommentEnd = "-->";
    private const string DefaultEllipsisReplacement = "/* ... */";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = false
    };

    public IReadOnlyList<SpecExample> ExtractDirectory(string directory)
    {
        var examples = new List<SpecExample>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.md")
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            examples.AddRange(Extract(Path.GetFileName(path),
                File.ReadAllText(path)));
        }
        return examples;
    }

    public IReadOnlyList<SpecExample> Extract(string mdFile, string markdown)
    {
        var lines = NormalizeNewlines(markdown).Split('\n');
        var examples = new List<SpecExample>();
        var pending = new List<PendingAnnotation>();
        var inOtherFence = false;

        for (var index = 0; index < lines.Length; index++)
        {
            var content = StripBlockquotePrefix(lines[index]);
            if (inOtherFence)
            {
                if (IsFence(content, out var language) && language.Length == 0)
                    inOtherFence = false;
                continue;
            }

            var marker = content.IndexOf(AnnotationPrefix,
                StringComparison.Ordinal);
            if (marker >= 0)
            {
                pending.Add(ReadAnnotation(lines, ref index, marker,
                    index + 1));
                continue;
            }

            if (!IsFence(content, out var fenceLanguage))
                continue;

            if (!fenceLanguage.Equals("csharp",
                    StringComparison.OrdinalIgnoreCase))
            {
                if (fenceLanguage.Length > 0)
                    inOtherFence = true;
                continue;
            }

            var codeLines = new List<string>();
            var closed = false;
            for (index++; index < lines.Length; index++)
            {
                var codeLine = StripBlockquotePrefix(lines[index]);
                if (IsFence(codeLine, out var closingLanguage) &&
                    closingLanguage.Length == 0)
                {
                    closed = true;
                    break;
                }
                codeLines.Add(codeLine);
            }

            if (pending.Count == 0)
                continue;

            var selected = pending[0];
            pending.Clear();
            if (!closed)
            {
                examples.Add(CreateFailure(mdFile, selected,
                    "csharp fence is not closed"));
                continue;
            }

            var code = string.Join("\n", codeLines);
            code = TransformCode(code, selected.Annotation);
            examples.Add(new SpecExample(mdFile, selected.Annotation, code,
                selected.SourceLine, selected.ParseFailure is null
                    ? null : "annotation-parse-error",
                selected.ParseFailure));
        }

        if (pending.Count > 0)
            examples.Add(CreateFailure(mdFile, pending[0],
                "no following csharp fence"));

        return examples;
    }

    private static PendingAnnotation ReadAnnotation(string[] lines,
        ref int index, int marker, int sourceLine)
    {
        var raw = new StringBuilder();
        var content = StripBlockquotePrefix(lines[index]);
        var remainder = content[(marker + AnnotationPrefix.Length)..];

        while (true)
        {
            var end = remainder.IndexOf(CommentEnd, StringComparison.Ordinal);
            if (end >= 0)
            {
                raw.Append(remainder[..end]);
                break;
            }

            raw.AppendLine(remainder);
            index++;
            if (index >= lines.Length)
                break;
            remainder = StripBlockquotePrefix(lines[index]);
        }

        try
        {
            var json = QuoteBarePropertyNames(raw.ToString().Trim());
            var annotation = JsonSerializer.Deserialize<SpecAnnotation>(json,
                JsonOptions) ?? throw new JsonException("annotation is null");
            return new PendingAnnotation(annotation, sourceLine, null);
        }
        catch (Exception exception) when (exception is JsonException or
                                            NotSupportedException)
        {
            return new PendingAnnotation(new SpecAnnotation(), sourceLine,
                exception.Message);
        }
    }

    private static SpecExample CreateFailure(string mdFile,
        PendingAnnotation pending, string details)
    {
        var combinedDetails = pending.ParseFailure is null
            ? details
            : $"{pending.ParseFailure}; {details}";
        return new SpecExample(mdFile, pending.Annotation, "",
            pending.SourceLine, "annotation-parse-error", combinedDetails);
    }

    private static string TransformCode(string code, SpecAnnotation annotation)
    {
        if (annotation.ReplaceEllipsis)
        {
            var replacements = annotation.CustomEllipsisReplacements ?? [];
            var replacementIndex = 0;
            var transformed = new StringBuilder(code.Length);
            for (var index = 0; index < code.Length; index++)
            {
                var width = code[index] == '…' ? 1 :
                    code.AsSpan(index).StartsWith("...", StringComparison.Ordinal)
                        ? 3 : 0;
                if (width == 0)
                {
                    transformed.Append(code[index]);
                    continue;
                }

                var replacement = replacementIndex < replacements.Length
                    ? replacements[replacementIndex]
                    : null;
                transformed.Append(replacement ?? DefaultEllipsisReplacement);
                replacementIndex++;
                index += width - 1;
            }
            code = transformed.ToString();
        }

        return code.Replace("«", "", StringComparison.Ordinal)
            .Replace("»", "", StringComparison.Ordinal);
    }

    private static string QuoteBarePropertyNames(string json)
    {
        var normalized = new StringBuilder(json.Length + 32);
        var inString = false;
        var escaped = false;
        var objectDepth = 0;
        var arrayDepth = 0;
        var expectingProperty = false;

        for (var index = 0; index < json.Length; index++)
        {
            var character = json[index];
            if (inString)
            {
                normalized.Append(character);
                if (escaped)
                    escaped = false;
                else if (character == '\\')
                    escaped = true;
                else if (character == '"')
                    inString = false;
                continue;
            }

            if (character == '"')
            {
                inString = true;
                expectingProperty = false;
                normalized.Append(character);
                continue;
            }
            if (character == '{')
            {
                objectDepth++;
                expectingProperty = objectDepth == 1;
                normalized.Append(character);
                continue;
            }
            if (character == '}')
            {
                objectDepth--;
                normalized.Append(character);
                continue;
            }
            if (character == '[')
            {
                arrayDepth++;
                normalized.Append(character);
                continue;
            }
            if (character == ']')
            {
                arrayDepth--;
                normalized.Append(character);
                continue;
            }
            if (character == ',' && objectDepth == 1 && arrayDepth == 0)
            {
                expectingProperty = true;
                normalized.Append(character);
                continue;
            }

            if (expectingProperty && objectDepth == 1 && arrayDepth == 0 &&
                (char.IsLetter(character) || character == '_'))
            {
                var end = index + 1;
                while (end < json.Length &&
                       (char.IsLetterOrDigit(json[end]) || json[end] == '_'))
                    end++;
                var probe = end;
                while (probe < json.Length && char.IsWhiteSpace(json[probe]))
                    probe++;
                if (probe < json.Length && json[probe] == ':')
                {
                    normalized.Append('"').Append(json, index, end - index)
                        .Append('"');
                    index = end - 1;
                    expectingProperty = false;
                    continue;
                }
            }

            normalized.Append(character);
        }

        return normalized.ToString();
    }

    private static bool IsFence(string line, out string language)
    {
        var trimmed = line.Trim();
        var delimiterLength = 0;
        while (delimiterLength < trimmed.Length &&
               trimmed[delimiterLength] == '`')
            delimiterLength++;
        if (delimiterLength < 3)
        {
            language = "";
            return false;
        }
        language = trimmed[delimiterLength..].Trim();
        return true;
    }

    private static string StripBlockquotePrefix(string line)
    {
        var index = 0;
        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;
        if (index >= line.Length || line[index] != '>')
            return line;
        index++;
        if (index < line.Length && line[index] == ' ')
            index++;
        return line[index..];
    }

    private static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private sealed record PendingAnnotation(SpecAnnotation Annotation,
        int SourceLine, string? ParseFailure);
}
