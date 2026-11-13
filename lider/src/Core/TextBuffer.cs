// 開いていない file の中身を行に分ける (検索が宣言を拾うのに読む)。
// 開いた file の本文は エディタが持つ (Document)。
using System.Collections.Generic;

namespace LiderCore;

public static class TextBuffer
{
    // 改行で分けた行 (CRLF の file は行の末尾の CR を除く)
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
