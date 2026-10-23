// Io.SaveText / Io.LoadText の往復。親ディレクトリを作り、NUL を含む文字列もそのまま戻り、同じ内容を書き直すと version が変わらない。ディレクトリへの保存と NUL を含む path は error で、既存の内容は壊れない。
using System;
using Lub;

public static class TestIoText
{
    static int step;

    // テストが終わったあとも残る (C# から file は消せない)。.lub/ は gitignore 済み
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
