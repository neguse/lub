// Rewind を記録している間に、VM を起動し直す編集 (static の initializer の変更) をする。
// 起動し直した VM で、前の VM の控えが残っていないこと、輪を一周して止めて戻せることを overlay (TestRewindRestartTool) が見る。
// 自分のソースを書き換えるので、native gate がこの file と overlay と Check.cs を一時ディレクトリに写して起動する。
// 書き換える file の path は env LUB_RELOAD_TEST_FILE。
using System;
using Lub;

public static class TestRewindRestart
{
    // 書き換えで 2 になり、VM が起動し直す
    static int Phase = 1;
    static int frames;
    static float waited;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "vulkan" });
    }

    public static void OnFrame(float dt)
    {
        frames++;
        TestRewindRestartTool.GamePhase = Phase;
        TestRewindRestartTool.GameFrames = frames;
        if (Phase != 1)
            return;
        // 輪 (10 frame) が埋まってから書き換える
        if (frames == 12)
        {
            var path = Environment.GetEnvironmentVariable("LUB_RELOAD_TEST_FILE");
            Check.That(path != null, "LUB_RELOAD_TEST_FILE is not set");
            Io.LoadText(path!, out var text, out _, out var status, out var error);
            Check.That(status == Io.Status.Ready && text != null, "cannot read the source: " + error);
            var from = "Phase = " + "1;";
            Check.That(text!.Contains(from), "the source has no initializer to change");
            Io.SaveText(path!, text.Replace(from, "Phase = " + "2;"));
        }
        if (frames > 12)
        {
            waited += dt;
            Check.That(waited < 60, "the VM did not start again in 60 seconds");
        }
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
