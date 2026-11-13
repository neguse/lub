// 画像と音の file を Ctrl+P から板に出す。
//   - 画像と音は検索に並び、選ぶと窓を開かず板に出す。画像は大きさ、音は長さを出し、音は 1 度鳴らす
//   - 確定しても出したままで、Escape で閉じる (窓は閉じない)。検索を取り消すと閉じる
//   - Alt+Delete は確認して file を消し、板を閉じる
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestAsset
{
    static int stage;
    static float next;
    static bool failed;

    public static void OnInit()
    {
        TestInit();
        Lider.OnInit();
    }

    public static void OnEvent(EventData e)
    {
        Lider.OnEvent(e);
    }

    public static void OnQuit()
    {
        Lider.OnQuit();
    }

    public static void OnFrame(float dt)
    {
        TestFrame(dt);
        Lider.OnFrame(dt);
    }

    static void TestInit()
    {
        App.Config(new ConfigOpts { Width = 640, Height = 360 });
    }

    static void TestFrame(float dt)
    {
        if (failed || Sys.Seconds() < next)
            return;
        next = Sys.Seconds() + 0.3f;
        var w = Lider.Work!;
        var s = Lider.Search;
        var board = Lider.AssetBoard;
        if (stage == 0)
        {
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!);
            w.Rescan();
            s.Open(w);
            s.Type(w, "pic");
            var count = w.Windows.Count;
            Check(s.Results.Count > 0 && s.Results[0].Asset && s.Results[0].Text == "pic.png", "the image is not listed");
            s.Move(w, 0);
            Check(w.ShownAsset == w.PathOf("pic.png") && w.Windows.Count == count, "choosing the image: " + w.ShownAsset);
        }
        else if (stage == 1)
        {
            Check(board.Info == "8 x 4", "the image info: " + board.Info);
            s.Cancel(w);
            Check(w.ShownAsset == "", "Cancel kept the image");
            s.Open(w);
            s.Type(w, "beep");
            s.Commit(w);
            Check(w.ShownAsset == w.PathOf("beep.wav") && !s.Shown, "Commit did not keep the sound: " + w.ShownAsset);
        }
        else if (stage == 2)
        {
            Check(board.Info == "0.10 秒", "the sound info: " + board.Info);
            Check(board.Played, "the sound did not play");
            var windows = w.Windows.Count;
            Press(w, Keys.Escape);
            Check(w.ShownAsset == "" && w.Windows.Count == windows, "Escape: " + w.ShownAsset + " windows " + w.Windows.Count);
            w.ShownAsset = w.PathOf("beep.wav");
            Lider.OnEvent(new EventData { Kind = EventKind.KeyDown, Key = Keys.Delete, Mods = Keys.ModAlt, Typed = 0, Plain = 0 });
            Lider.OnEvent(new EventData { Kind = EventKind.KeyUp, Key = Keys.Delete, Mods = 0, Typed = 0, Plain = 0 });
            Check(EditorHost.Modal(), "Alt+Delete did not ask");
            Press(w, 28); // y
            w.Rescan();
            Check(w.ShownAsset == "" && !w.AssetFiles.Contains(w.PathOf("beep.wav")), "the sound was not deleted");
            if (!failed)
            {
                Console.WriteLine("PASS");
                App.Quit(0);
            }
        }
        stage++;
    }

    static void Press(Workspace w, int key)
    {
        w.Key(key, true);
        w.Key(key, false);
    }

    static void Check(bool ok, string message)
    {
        if (ok || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}
