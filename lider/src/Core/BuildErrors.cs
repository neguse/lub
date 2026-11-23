// Error lines when tcs stops the build with a compile error (lub's OnBuildFailed).
// The line format is `path(line,col): error ID: message`. Lines in another format become errors without a path
using System.Collections.Generic;

namespace LiderCore;

public class BuildError
{
    // The path passed to tcs (Workspace converts it to the list's path). "" for lines in another format
    public string Path;
    // From 0. -1 for lines in another format
    public readonly int Line;
    // The latter part of `error ID: message` (the line as is for lines in another format)
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
