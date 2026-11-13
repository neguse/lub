// 操作の案内 (Hints.For) を確かめる。
//   - 指している物ごとに、そのとき効く操作だけが並ぶ (開けない行では開閉を出さない)
//   - VR はコントローラーのボタン、非 VR は key とマウス
//   - インスペクタの出し入れはいつも、frame を送る操作は止めている間だけ
//   - エラーの行へ飛ぶ操作は、飛べるエラーがある間だけ
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;

public static class TestHints
{
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
        Expect(new HintContext { Vr = true, Target = HintTarget.Window },
            "Trigger R:カーソルを置く・引いて選ぶ|StickUpDown R:scroll|Face X:インスペクタを出す");
        Expect(new HintContext { Vr = true, Target = HintTarget.Inspect, InspectShown = true },
            "StickUpDown R:scroll|Face X:インスペクタをしまう");
        Expect(new HintContext { Vr = true, Target = HintTarget.Inspect, InspectShown = true, Openable = true, Open = true },
            "Trigger R:閉じる|StickUpDown R:scroll|Face X:インスペクタをしまう");
        Expect(new HintContext { Vr = true, Target = HintTarget.Nothing, Paused = true },
            "Trigger R:指した物を描いたコードを開く|Face X:インスペクタを出す|StickLeftRight L:1 frame 戻す・進める");
        Expect(new HintContext { Vr = false, Target = HintTarget.Inspect, InspectShown = true, Openable = true, Jump = true },
            "MouseLeft:宣言へ飛ぶ|MouseWheel:scroll|KeyCap F2:インスペクタをしまう|KeyCap F11:窓を画面いっぱいに広げる|KeyCap F5:ゲームを止める|KeyCap Shift+F5:ゲームを最初から作り直す");
        Expect(new HintContext { Vr = false, Target = HintTarget.Window, Paused = true },
            "MouseLeft:カーソルを置く・引いて選ぶ|KeyCap 右ドラッグ:窓の並びを変える (ホイールで大きさ)|MouseWheel:scroll|KeyCap Ctrl+P:file と宣言を探す・作る|KeyCap F12 Ctrl+-:定義へ飛ぶ・戻る|KeyCap Ctrl+Tab:隣のタブへ|KeyCap Ctrl+Alt+←→:タブを隣の窓へ移す|KeyCap Alt+←→:隣の窓へ|KeyCap Alt+Del:file を消す|KeyCap Esc Ctrl+W:タブを閉じる|KeyCap F2:インスペクタを出す|KeyCap F11:窓を画面いっぱいに広げる|KeyCap F5:ゲームを続ける|KeyCap Shift+F5:ゲームを最初から作り直す|KeyCap F6/F7:1 frame 戻す・進める");
        Expect(new HintContext { Vr = true, Target = HintTarget.Window, InstanceMark = true },
            "Trigger R:この個体に絞り込む・やめる|StickUpDown R:scroll|Face X:インスペクタを出す");
        Expect(new HintContext { Vr = true, Target = HintTarget.Window, MoreMark = true },
            "Trigger R:続きの個体を並べる|StickUpDown R:scroll|Face X:インスペクタを出す");
        Expect(new HintContext { Vr = false, Target = HintTarget.Inspect, InspectShown = true, Openable = true, Scope = true },
            "MouseLeft:ここに絞り込む|MouseWheel:scroll|KeyCap F2:インスペクタをしまう|KeyCap F11:窓を画面いっぱいに広げる|KeyCap F5:ゲームを止める|KeyCap Shift+F5:ゲームを最初から作り直す");
        Expect(new HintContext { Vr = true, Target = HintTarget.Inspect, InspectShown = true, Crumb = true },
            "Trigger R:ここまで戻る|StickUpDown R:scroll|Face X:インスペクタをしまう");
        Expect(new HintContext { Vr = false, Target = HintTarget.Nothing, What = new List<string> { "Pipe.Draw (Pipes.cs:34)", "← Flappy.Draw (Flappy.cs:86)" } },
            "None:Pipe.Draw (Pipes.cs:34)|None:← Flappy.Draw (Flappy.cs:86)|MouseLeft:このコードを開く");
        Expect(new HintContext { Vr = false, Target = HintTarget.Close },
            "MouseLeft:窓を閉じる|KeyCap F2:インスペクタを出す|KeyCap F11:窓を画面いっぱいに広げる|KeyCap F5:ゲームを止める|KeyCap Shift+F5:ゲームを最初から作り直す");
        Expect(new HintContext { Vr = false, Target = HintTarget.Close, Errors = true },
            "MouseLeft:窓を閉じる|KeyCap F2:インスペクタを出す|KeyCap F11:窓を画面いっぱいに広げる|KeyCap F5:ゲームを止める|KeyCap Shift+F5:ゲームを最初から作り直す|KeyCap F8:エラーの行へ");
        Expect(new HintContext { Vr = false, Target = HintTarget.Nothing },
            "MouseLeft:指した物を描いたコードを開く|KeyCap 右ドラッグ:窓の列を動かす|KeyCap Ctrl+P:file と宣言を探す・作る|KeyCap F4:音を切る|KeyCap F1:ゲームを触る (Shift+F1 でゲームだけ)|KeyCap F2:インスペクタを出す|KeyCap F11:窓を画面いっぱいに広げる|KeyCap F5:ゲームを止める|KeyCap Shift+F5:ゲームを最初から作り直す");
        // 札は指す点の右下に置き、画面の端を越えるなら上か左に置く
        var card = new HintCard();
        card.Place(new CylinderPoint(0, 0), 0, -1, 1);
        Check(card.Angle > 0 && card.Y < 0, "the card is not at the lower right");
        card.Place(new CylinderPoint(0, -0.99f), 0, -1, 1);
        Check(card.Y > -0.99f, "the card is not above the point at the bottom edge");
        card.Place(new CylinderPoint(0.99f, 0), 0, -1, 1);
        Check(card.Angle < 0.99f, "the card is not left of the point at the right edge");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static void Expect(HintContext c, string want)
    {
        var got = "";
        foreach (var h in Hints.For(c))
            got += (got == "" ? "" : "|") + Name(h.Icon) + (h.Key == "" ? "" : " " + h.Key) + ":" + h.Label;
        Check(got == want, "got " + got + ", want " + want);
    }

    static string Name(HintIcon icon)
    {
        switch (icon)
        {
            case HintIcon.Face: return "Face";
            case HintIcon.Trigger: return "Trigger";
            case HintIcon.StickUpDown: return "StickUpDown";
            case HintIcon.StickLeftRight: return "StickLeftRight";
            case HintIcon.KeyCap: return "KeyCap";
            case HintIcon.MouseLeft: return "MouseLeft";
            case HintIcon.None: return "None";
            default: return "MouseWheel";
        }
    }

    // 最初の失敗だけを出して終わる
    static void Check(bool cond, string message)
    {
        if (cond || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}
