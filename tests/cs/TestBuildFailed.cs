// native の watch で、compile error の保存では OnBuildFailed に error の行が届き、ゲームは前のコードのまま動くこと。
// 直して保存すると reload chunk が当たり、OnReload が呼ばれる。
// 自分のソースを書き換えるので、native gate がこの file と Check.cs を一時ディレクトリに写し、そこの TestBuildFailed.csproj を起動する。
// 書き換える file の path は env LUB_RELOAD_TEST_FILE。
using System;
using Lub;

public static class TestBuildFailed
{
    static int frames;
    static int reloads;
    static int failures;
    static string errors = "";
    static float waited;
    static bool fixedSaved;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "vulkan" });
    }

    // compile error の版を挟んで、2 に書き換わる
    static int Value()
    {
        return 1;
    }

    public static void OnReload()
    {
        reloads++;
    }

    public static void OnBuildFailed(string e)
    {
        failures++;
        errors = e;
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        frames++;
        var path = Environment.GetEnvironmentVariable("LUB_RELOAD_TEST_FILE");
        Check.That(path != null, "LUB_RELOAD_TEST_FILE is not set");
        if (frames == 1)
        {
            Check.That(Value() == 1, "frames went back to 1 after the edit (the VM started again or the statics were reset)");
            Replace(path!, "return " + "1;", "return " + "missing;");
            return;
        }
        waited += dt;
        Check.That(waited < 60, "nothing arrived in 60 seconds (failures " + failures + ", reloads " + reloads + ")");
        if (!fixedSaved)
        {
            if (failures == 0)
                return;
            Check.That(errors.Contains("CS0103") && errors.EndsWith("\n"), "OnBuildFailed got '" + errors + "'");
            Check.That(reloads == 0 && Value() == 1, "the broken save changed the running code");
            Replace(path!, "return " + "missing;", "return " + "2;");
            fixedSaved = true;
            return;
        }
        if (Value() == 1)
            return;
        Check.That(failures == 1 && reloads == 1, "OnBuildFailed ran " + failures + " times and OnReload " + reloads + " times, want 1 and 1");
        Check.Pass();
    }

    static void Replace(string path, string from, string to)
    {
        Io.LoadText(path, out var text, out _, out var status, out var error);
        Check.That(status == Io.Status.Ready && text != null, "cannot read the source: " + error);
        Check.That(text!.Contains(from), "the source has no '" + from + "'");
        Io.SaveText(path, text.Replace(from, to));
    }
}
