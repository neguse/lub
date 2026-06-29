using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TinyCs;

public static class SourceMapResolver
{
    private static readonly Regex LuaFrameRegex = new(
        @"(?<path>(?:[A-Za-z]:)?[^:\r\n]+?\.lua):(?<line>\d+):",
        RegexOptions.Compiled);

    public static string AnnotateStackTrace(string stackTrace,
        string sourceMapJson)
    {
        var mappings = Parse(sourceMapJson);
        var sb = new StringBuilder();
        using var reader = new StringReader(stackTrace);

        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            sb.AppendLine(AnnotateLine(line, mappings));
        }

        return sb.ToString();
    }

    public static (string File, int Line)? LookupNearest(string sourceMapJson,
        int luaLine) => LookupNearest(Parse(sourceMapJson), luaLine);

    private static string AnnotateLine(string line,
        SortedDictionary<int, (string File, int Line)> mappings)
    {
        var match = LuaFrameRegex.Match(line);
        if (!match.Success
            || !int.TryParse(match.Groups["line"].Value, out var luaLine))
        {
            return line;
        }

        var entry = LookupNearest(mappings, luaLine);
        return entry == null
            ? line
            : $"{line}  --> {entry.Value.File}:{entry.Value.Line}";
    }

    private static (string File, int Line)? LookupNearest(
        SortedDictionary<int, (string File, int Line)> mappings, int luaLine)
    {
        foreach (var kv in mappings.Reverse())
        {
            if (kv.Key <= luaLine)
                return kv.Value;
        }

        return null;
    }

    private static SortedDictionary<int, (string File, int Line)> Parse(
        string sourceMapJson)
    {
        var mappings = new SortedDictionary<int, (string File, int Line)>();
        using var doc = JsonDocument.Parse(sourceMapJson);
        if (!doc.RootElement.TryGetProperty("mappings", out var mapElement))
            return mappings;

        foreach (var prop in mapElement.EnumerateObject())
        {
            if (!int.TryParse(prop.Name, out var luaLine))
                continue;
            if (!prop.Value.TryGetProperty("file", out var fileElement)
                || !prop.Value.TryGetProperty("line", out var lineElement))
            {
                continue;
            }

            var file = fileElement.GetString() ?? "";
            var csLine = lineElement.GetInt32();
            mappings[luaLine] = (file, csLine);
        }

        return mappings;
    }
}
