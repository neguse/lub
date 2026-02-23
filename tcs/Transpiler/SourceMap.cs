using System.Text;
using System.Text.Json;

namespace TinyCs;

public class SourceMap
{
    // LuaLine -> (CsFile, CsLine)
    private readonly SortedDictionary<int, (string File, int Line)> _mappings = new();

    public void Add(int luaLine, string csFile, int csLine)
    {
        _mappings[luaLine] = (csFile, csLine);
    }

    public (string File, int Line)? Lookup(int luaLine)
    {
        return _mappings.TryGetValue(luaLine, out var entry) ? entry : null;
    }

    public int Count => _mappings.Count;

    public string ToJson()
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine("  \"version\": 1,");
        sb.AppendLine("  \"mappings\": {");
        bool first = true;
        foreach (var (luaLine, (file, csLine)) in _mappings)
        {
            if (!first) sb.AppendLine(",");
            first = false;
            var escapedFile = JsonEncodedText.Encode(file);
            sb.Append($"    \"{luaLine}\": {{\"file\": \"{escapedFile}\", \"line\": {csLine}}}");
        }
        sb.AppendLine();
        sb.AppendLine("  }");
        sb.Append("}");
        return sb.ToString();
    }
}
