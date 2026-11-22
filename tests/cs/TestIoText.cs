// Round trip of Io.SaveText / Io.LoadText.
// Parent directories are created, a string containing NUL comes back as is, and rewriting the same contents does not change the version.
// Saving to a directory and a path containing NUL are errors, and the existing contents are not corrupted.
using System;
using Lub;

public static class TestIoText
{
    static int step;

    // Remains after the test finishes (C# cannot delete files).
    // .lub/ is already in gitignore
    const string root = "tests/cs/.lub/io_text";
    const string path = root + "/nested/scores.txt";

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "vulkan" });
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        var s = step;
        step++;
        if (s == 0)
        {
            var original = "得点: 123\n\0end";
            Io.SaveText(path, original);
            Io.LoadText(path, out var text, out var version, out var status, out _);
            Check.That(status == Io.Status.Ready && text == original, "original text did not round trip");
            Io.SaveText(path, "replacement");
            Io.LoadText(path, out var replaced, out var revision, out var ready, out _);
            Check.That(ready == Io.Status.Ready && replaced == "replacement" && revision != version, "replaced text or revision");
            Io.SaveText(path, "replacement");
            Io.LoadText(path, out var same, out var sameRevision, out _, out _);
            Check.That(same == "replacement" && sameRevision == revision, "same text must keep the revision");
        }
        else if (s == 1)
        {
            Check.Error("save to a directory", "io.save_text");
            Io.SaveText(root + "/nested", "cannot replace a directory");
        }
        else if (s == 2)
        {
            Io.LoadText(path, out var text, out _, out _, out _);
            Check.That(text == "replacement", "failed save must keep the file");
            Check.Error("save to a path with NUL", "io.save_text");
            Io.SaveText(path + "\0suffix", "invalid");
        }
        else
        {
            Io.SaveText(path, "");
            Io.LoadText(path, out var empty, out _, out var status, out _);
            Check.That(status == Io.Status.Ready && empty == "", "empty text must be ready and empty");
            Check.Pass();
        }
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
