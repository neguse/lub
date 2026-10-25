// save/<名前>/... の保存場所。
// native は保存先が $XDG_DATA_HOME/lub-test/ になることを native gate が見る (1 回の実行で保存して読む)。
// web は verify (A9) が LUB_SAVE_TEST=write と read で page を 2 回開き、page をまたいで残ることを見る。
// web の保存場所は IndexedDB から読み終わるまで Pending なので、どちらもまず読み込みが Pending でなくなるのを待つ。
// 名前の無い path、file の無い path、.. を含む path は error。
// Io.ListDir も save/<名前> と save/<名前>/<ディレクトリ> を同じ場所で引く。
using System;
using Lub;

public static class TestSaveDir
{
    const string path = "save/lub-test/scores.txt";
    static readonly string? mode = Environment.GetEnvironmentVariable("LUB_SAVE_TEST");
    static int frame, step;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "vulkan" });
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        frame++;
        if (step == 0)
        {
            Io.LoadText(path, out var text, out _, out var status, out _);
            if (status == Io.Status.Pending)
            {
                if (frame > 600)
                    Check.Fail("the save directory stayed pending");
                return;
            }
            if (mode == "read")
            {
                Check.That(status == Io.Status.Ready && text == "saved", "the saved text must be read back, got " + status + " " + text);
                Check.Pass();
                return;
            }
            Io.SaveText(path, "saved");
            Io.LoadText(path, out var saved, out _, out var ready, out _);
            Check.That(ready == Io.Status.Ready && saved == "saved", "the saved text did not round trip");
            Io.LoadText("save/lub-test/../escape.txt", out _, out _, out var outside, out _);
            Check.That(outside == Io.Status.Error, "a load out of the save directory must be an error");
            Io.SaveText("save/lub-test/slots/1.txt", "1");
            Check.That(Names(Io.ListDir("save/lub-test")) == "scores.txt slots/ ", "listing of the save directory was '" + Names(Io.ListDir("save/lub-test")) + "'");
            Check.That(Names(Io.ListDir("save/lub-test/slots")) == "1.txt ", "listing of a directory in the save directory");
            Check.That(Names(Io.ListDir("save/lub-test/")) == "scores.txt slots/ ", "listing of the save directory with a trailing '/'");
            step = 1;
        }
        else if (step == 1)
        {
            step = 2;
            Check.Error("save path without a file", "io.save_text");
            Io.SaveText("save/lub-test", "x");
        }
        else if (step == 2)
        {
            step = 3;
            Check.Error("save path without a name", "io.save_text");
            Io.SaveText("save//scores.txt", "x");
        }
        else if (step == 3)
        {
            step = 4;
            Check.Error("save path out of the save directory", "io.save_text");
            Io.SaveText("save/lub-test/../escape.txt", "x");
        }
        else if (step == 4)
        {
            step = 5;
            Check.Error("listing out of the save directory", "io.list_dir");
            Io.ListDir("save/lub-test/..");
        }
        else if (frame > 60 || mode == null)
        {
            // web は IndexedDB への書き込みが終わるまで少し待ってから閉じる
            Check.Pass();
        }
    }

    static string Names(System.Collections.Generic.List<DirEntry> entries)
    {
        var names = "";
        foreach (var e in entries)
            names += e.Name + (e.IsDir ? "/" : "") + " ";
        return names;
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
