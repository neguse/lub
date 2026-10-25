// native の hot reload で、reload chunk を当てた後に OnReload が呼ばれ、static の値が残ること。
// 自分のソースを書き換えるので、native gate がこの file と Check.cs を一時ディレクトリに写し、そこの TestReload.csproj を起動する。
// 書き換える file の path は env LUB_RELOAD_TEST_FILE。
using System;
using Lub;

public static class TestReload
{
    static int frames;
    static int reloads;
    static float waited;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "vulkan" });
    }

    // reload で 2 に書き換わる
    static int Value()
    {
        return 1;
    }

    public static void OnReload()
    {
        reloads++;
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        frames++;
        if (frames == 1)
        {
            var path = Environment.GetEnvironmentVariable("LUB_RELOAD_TEST_FILE");
            Check.That(path != null, "LUB_RELOAD_TEST_FILE is not set");
            Io.LoadText(path!, out var text, out _, out var status, out var error);
            Check.That(status == Io.Status.Ready && text != null, "cannot read the source: " + error);
            var from = "return " + "1;";
            Check.That(text!.Contains(from), "the source has no value to change");
            Io.SaveText(path!, text.Replace(from, "return " + "2;"));
            return;
        }
        waited += dt;
        if (Value() == 1)
        {
            Check.That(reloads == 0, "OnReload ran before the reload chunk");
            Check.That(waited < 60, "the reload chunk did not arrive in 60 seconds");
            return;
        }
        Check.That(reloads == 1, "OnReload ran " + reloads + " times, want 1");
        Check.That(frames > 2, "the frame count went back to its initial value");
        Check.Pass();
    }
}
