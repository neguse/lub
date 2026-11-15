// 操作の案内 (HintRules.For) を確かめる。
//   - 指している物ごとに、そのとき効く操作だけが並ぶ (開けない行では開閉を出さない)
//   - VR はコントローラーのボタン、非 VR は key とマウス
//   - インスペクタの出し入れはいつも、frame を送る操作は止めている間だけ
//   - エラーの行へ飛ぶ操作は、飛べるエラーがある間だけ
//   - 案内のウィンドウを置く端から積む規則 (Stack) は、大きさを変えずに下から積む
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
            "StickUpDown R:scroll|Face X:インスペクタへ移る (もう一度でしまう)");
        Expect(new HintContext { Vr = true, Target = HintTarget.Inspect, InspectShown = true, Openable = true, Open = true },
            "Trigger R:閉じる|StickUpDown R:scroll|Face X:インスペクタへ移る (もう一度でしまう)");
        Expect(new HintContext { Vr = true, Target = HintTarget.Nothing, Paused = true },
            "Trigger R:指した物を描いたコードを開く|Face X:インスペクタを出す|StickLeftRight L:1 frame 戻す・進める");
        Expect(new HintContext { Vr = false, Target = HintTarget.Inspect, InspectShown = true, Openable = true, Jump = true },
            "MouseLeft:宣言へ飛ぶ|MouseWheel:scroll|KeyCap Alt+I:インスペクタへ移る (もう一度でしまう)|KeyCap Alt+Enter:窓を画面いっぱいに広げる|KeyCap F5:ゲームを止める|KeyCap Shift+F5:ゲームを最初から作り直す");
        Expect(new HintContext { Vr = false, Target = HintTarget.Window, Paused = true },
            "MouseLeft:カーソルを置く・引いて選ぶ|KeyCap 右ドラッグ:窓の並びを変える (ホイールで大きさ)|MouseWheel:scroll|KeyCap Ctrl+P:file と宣言と命令を探す|KeyCap Ctrl+B Ctrl+-:定義へ飛ぶ・戻る|KeyCap Ctrl+Tab:隣のタブへ|KeyCap Ctrl+Alt+←→:タブを隣の窓へ移す|KeyCap Alt+←→↑↓:隣のウィンドウへ|KeyCap Ctrl+W:タブを閉じる|KeyCap Alt+I:インスペクタを出す|KeyCap Alt+Enter:窓を画面いっぱいに広げる|KeyCap F5:ゲームを続ける|KeyCap Shift+F5:ゲームを最初から作り直す|KeyCap F6/F7:1 frame 戻す・進める");
        Expect(new HintContext { Vr = true, Target = HintTarget.Window, InstanceMark = true },
            "Trigger R:この個体に絞り込む・やめる|StickUpDown R:scroll|Face X:インスペクタを出す");
        Expect(new HintContext { Vr = true, Target = HintTarget.Window, MoreMark = true },
            "Trigger R:続きの個体を並べる|StickUpDown R:scroll|Face X:インスペクタを出す");
        Expect(new HintContext { Vr = false, Target = HintTarget.Inspect, InspectShown = true, Openable = true, Scope = true },
            "MouseLeft:ここに絞り込む|MouseWheel:scroll|KeyCap Alt+I:インスペクタへ移る (もう一度でしまう)|KeyCap Alt+Enter:窓を画面いっぱいに広げる|KeyCap F5:ゲームを止める|KeyCap Shift+F5:ゲームを最初から作り直す");
        Expect(new HintContext { Vr = true, Target = HintTarget.Inspect, InspectShown = true, Crumb = true },
            "Trigger R:ここまで戻る|StickUpDown R:scroll|Face X:インスペクタへ移る (もう一度でしまう)");
        Expect(new HintContext { Vr = false, Target = HintTarget.Nothing, What = new List<string> { "Pipe.Draw (Pipes.cs:34)", "← Flappy.Draw (Flappy.cs:86)" } },
            "None:Pipe.Draw (Pipes.cs:34)|None:← Flappy.Draw (Flappy.cs:86)|MouseLeft:このコードを開く");
        Expect(new HintContext { Vr = false, Target = HintTarget.Close },
            "MouseLeft:窓を閉じる|KeyCap Alt+I:インスペクタを出す|KeyCap Alt+Enter:窓を画面いっぱいに広げる|KeyCap F5:ゲームを止める|KeyCap Shift+F5:ゲームを最初から作り直す");
        Expect(new HintContext { Vr = false, Target = HintTarget.Close, Errors = true },
            "MouseLeft:窓を閉じる|KeyCap Alt+I:インスペクタを出す|KeyCap Alt+Enter:窓を画面いっぱいに広げる|KeyCap F5:ゲームを止める|KeyCap Shift+F5:ゲームを最初から作り直す|KeyCap Ctrl+E:エラーの行へ");
        Expect(new HintContext { Vr = false, Target = HintTarget.Nothing },
            "KeyCap 右ドラッグ:窓の列を動かす|KeyCap Ctrl+P:file と宣言と命令を探す|KeyCap F1:ゲームを触る|KeyCap Alt+I:インスペクタを出す|KeyCap Alt+Enter:窓を画面いっぱいに広げる|KeyCap F5:ゲームを止める|KeyCap Shift+F5:ゲームを最初から作り直す");
        // 端から積む規則は、右下の隅に寄せると、ウィンドウの大きさを変えずに、端から余白を空けて下から積む
        var screen = new Screen("test") { Size = new Vec2(4, 2), Layout = new Stack() };
        var a = new Window(new Hints(new HintCard())) { Size = new Vec2(1, 0.5f) };
        var b = new Window(new Hints(new HintCard())) { Size = new Vec2(0.6f, 0.2f) };
        screen.Windows.Add(a);
        screen.Windows.Add(b);
        screen.Arrange();
        var margin = new Stack().Margin;
        Near(a.Position!.Value.X + 0.5f, 2 - margin, "the first window is not at the right edge");
        Near(a.Position.Value.Y - 0.25f, -1 + margin, "the first window is not at the bottom edge");
        Near(b.Position!.Value.X + 0.3f, 2 - margin, "the second window is not at the right edge");
        Near(b.Position.Value.Y - 0.1f, a.Position.Value.Y + 0.25f + margin, "the second window is not above the first");
        Check(a.Size.X == 1 && b.Size.Y == 0.2f, "the layout changed the sizes");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    static void Expect(HintContext c, string want)
    {
        var got = "";
        foreach (var h in HintRules.For(c))
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
    static void Near(float got, float want, string message)
    {
        Check(Math.Abs(got - want) < 0.001f, message + ": " + got + ", want " + want);
    }

    static void Check(bool cond, string message)
    {
        if (cond || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}
