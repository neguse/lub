// Io.Stat は file とディレクトリの種類、大きさ、更新時刻を返し、無い path は null。
// Io.AbsolutePath は "." と ".." を畳んだ絶対 path を返し、無い path (途中のディレクトリを含む) はエラー。
// Sys.Seconds は frame をまたいで増える。
using System;
using Lub;

public static class TestIoStat
{
    const string root = "tests/cs/.lub/io_stat";
    static int step;
    static float firstSeconds;

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
            Io.SaveText(root + "/a.txt", "hello");
            var file = Io.Stat(root + "/a.txt");
            Check.That(file != null && !file.IsDir && file.Size == 5, "a.txt: " + (file == null ? "null" : file.IsDir + " " + file.Size));
            Check.That(file != null && file.Modified > 1700000000, "a.txt modified " + (file == null ? 0 : file.Modified));
            var dir = Io.Stat(root);
            Check.That(dir != null && dir.IsDir, "the directory is not a directory");
            Check.That(Io.Stat(root + "/missing.txt") == null, "a missing file has a stat");
            Io.AbsolutePath(root + "/../io_stat/a.txt", out var absolute);
            Check.That(absolute != null && absolute.StartsWith("/") && absolute.EndsWith("/tests/cs/.lub/io_stat/a.txt") && !absolute.Contains(".."),
                "absolute path was '" + absolute + "'");
            firstSeconds = Sys.Seconds();
        }
        else if (s == 1)
        {
            Check.Error("a missing path", "does not exist");
            Io.AbsolutePath(root + "/missing.txt", out _);
        }
        else if (s < 5)
        {
            // frame をまたいで時計が進む
        }
        else
        {
            Check.That(Sys.Seconds() > firstSeconds, "Sys.Seconds did not grow: " + firstSeconds + " -> " + Sys.Seconds());
            Check.Pass();
        }
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
