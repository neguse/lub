// Io.ListDir はディレクトリの中身を名前の昇順で返し、ディレクトリかどうかを付ける。
// "." と ".." は返さず、名前が '.' で始まるものは返す。
// ディレクトリでない path と空の path は error。
using System;
using Lub;

public static class TestIoListDir
{
    static int step;

    // テストが終わったあとも残る (C# から file は消せない)。
    // .lub/ は gitignore 済み
    const string root = "tests/cs/.lub/io_list_dir";

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
            Io.SaveText(root + "/b.txt", "b");
            Io.SaveText(root + "/a.cs", "a");
            Io.SaveText(root + "/sub/c.txt", "c");
            Io.SaveText(root + "/.hidden", "h");
            var entries = Io.ListDir(root);
            var names = "";
            foreach (var e in entries)
                names += e.Name + (e.IsDir ? "/" : "") + " ";
            Check.That(names == ".hidden a.cs b.txt sub/ ", "listing was '" + names + "'");
            var sub = Io.ListDir(root + "/sub");
            Check.That(sub.Count == 1 && sub[0].Name == "c.txt" && !sub[0].IsDir, "nested listing");
        }
        else if (s == 1)
        {
            Check.Error("a file is not a directory", "is not a directory");
            Io.ListDir(root + "/b.txt");
        }
        else if (s == 2)
        {
            Check.Error("a missing path", "is not a directory");
            Io.ListDir(root + "/missing");
        }
        else if (s == 3)
        {
            Check.Error("an empty path", "invalid path");
            Io.ListDir("");
        }
        else
        {
            Check.Pass();
        }
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
