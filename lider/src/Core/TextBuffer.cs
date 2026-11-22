// Splits the contents of a file that is not open into lines (read by search to pick up declarations).
// The text of open files is held by the editor (Document).
using System.Collections.Generic;

namespace LiderCore;

public static class TextBuffer
{
    // Lines split at newlines (for CRLF files, the trailing CR is removed from each line)
    public static List<string> Lines(string text)
    {
        var lines = new List<string>();
        var parts = text.Split('\n');
        var crlf = parts.Length > 1 && parts[0].EndsWith("\r");
        foreach (var line in parts)
            lines.Add(crlf && line.EndsWith("\r") ? Utf8.Sub(line, 0, Utf8.Bytes(line) - 1) : line);
        return lines;
    }
}
