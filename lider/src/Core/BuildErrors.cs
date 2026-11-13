// tcs が compile error で build を止めたときの error の行 (lub の OnBuildFailed)。
// 行の形は `path(line,col): error ID: message`。形の違う行は、path の無い error にする
using System.Collections.Generic;

namespace LiderCore;

public class BuildError
{
    // tcs に渡した path (Workspace が一覧の path に直す)。形の違う行は ""
    public string Path;
    // 0 から。形の違う行は -1
    public readonly int Line;
    // `error ID: message` の後ろの部分 (形の違う行は行のまま)
    public readonly string Message;

    public BuildError(string path, int line, string message)
    {
        Path = path;
        Line = line;
        Message = message;
    }

    public static List<BuildError> Parse(string text)
    {
        var errors = new List<BuildError>();
        foreach (var raw in text.Split('\n'))
        {
            var s = raw.Trim();
            if (s == "")
                continue;
            errors.Add(ParseLine(s));
        }
        return errors;
    }

    static BuildError ParseLine(string s)
    {
        var close = Utf8.IndexOf(s, "): ");
        var open = close - 1;
        while (open > 0 && Utf8.At(s, open) != '(')
            open--;
        var comma = open > 0 ? Utf8.IndexOf(s, ",", open) : -1;
        if (open <= 0 || comma < 0 || comma > close || !int.TryParse(Utf8.Sub(s, open + 1, comma - open - 1), out var line))
            return new BuildError("", -1, s);
        var message = Utf8.Sub(s, close + 3);
        var at = Utf8.IndexOf(message, ": ");
        if (message.StartsWith("error ") && at > 0)
            message = Utf8.Sub(message, at + 2);
        return new BuildError(Utf8.Sub(s, 0, open), line - 1, message);
    }
}
