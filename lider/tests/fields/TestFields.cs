// フィールドの宣言の行に出す今の値と、class の宣言の行の個体の印 (FieldValues)。値と個体はゲーム (game/Game.cs) の物。
//   - static はその値。const は出さない。範囲の外の行は出さない
//   - instance が 1 つならその値で、印は出さない
//   - 幾つもあれば class の行に目印を並べ、選ぶまでフィールドの行には何も出さない。Toggle で選び、もう一度で解く。Select で行の path か祖先の個体を選ぶ
//   - 4 個を超えると「… 全 N 個」が付き、NextPage で続きを並べ、最後の次は最初。選んだ個体は範囲の外でも入る
//   - 要素が減って並びがずれても、同じ個体を指し続ける (目印も値も変わらない)。path が変わっても光らない
//   - 選んだ個体が辿れなくなると選択を解き、別の個体へ移らない
//   - しばらく変わらなかった値が変わると光り、毎 frame 変わる値は光らない
//   - 数のフィールドは、出している間のゲームの frame ごとの値を TraceLog.Fade 個まで持つ。文字のフィールドは持たない。個体は要素の並びが変わっても追う
//   - 窓の class の行に印が出て、印を押すと個体を選ぶ
//   - 探し直す前に並びがずれても、別の個体の値を出さない
// ゲームは決めた frame で Depot.Motes と Depot.Calm を変える。test はゲームの値を見て、変わったのを見てから次を確かめる。
// 見える値は 1 frame 遅れる (lider がゲームを進めるのは TestFrame の後)。
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestFields
{
    static readonly FieldValues fields = new FieldValues();
    static readonly List<Declaration> decls = new List<Declaration>
    {
        new Declaration { Name = "Depot.Limit", Line = 0, Const = true },
        new Declaration { Name = "Depot.Calm", Line = 1 },
        new Declaration { Name = "Depot.Ticks", Line = 2 },
        new Declaration { Name = "Solo.Power", Line = 3 },
        new Declaration { Name = "Mote.Hp", Line = 4 },
        new Declaration { Name = "Mote.Name", Line = 7 },
        new Declaration { Name = "Depot", Line = 5, IsType = true },
        new Declaration { Name = "Mote", Line = 6, IsType = true },
    };
    static int frames;
    static bool failed;
    // 確かめている段と、その段で frame を数え始めた frame (ゲームの変化を見た frame か、段に入った frame。まだなら 0)
    static int stage;
    static int seenAt;
    // 最初の frame の Depot.Ticks と GameApp.Frames
    static float firstTicks;
    static int firstGame;

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
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    static void TestFrame(float dt)
    {
        // ゲームの build と、ゲームが最初の個体を置くのを待つ
        if (failed || GameApp.Frames == 0 || (frames == 0 && Count() != 3))
            return;
        frames++;
        if (frames > 900)
        {
            Check(false, "stage " + stage + " did not finish: " + Count() + " motes");
            return;
        }
        // 並びがずれたのを見た frame は、探し直す (Refresh) 前に確かめる
        if (stage == 0 && frames > 1 && Count() == 2)
        {
            Check(Mote5() == "5:12", "the selection read another instance before rediscovering: " + Mote5());
            Next();
            seenAt = frames;
        }
        fields.Refresh();
        var shown = fields.For(decls, 1, 100);
        if (frames == 1)
        {
            firstTicks = float.Parse(shown[1].Text);
            firstGame = GameApp.Frames;
            Check(Texts(shown) == "2:0 3:" + shown[1].Text + " 4:7 7:" + Tag(0) + " " + Tag(1) + " " + Tag(2), "got " + Texts(shown));
            Check(Texts(fields.For(decls, 4, 4)) == "4:7", "lines out of the range are shown: " + Texts(fields.For(decls, 4, 4)));
            fields.Toggle("Mote", Id(1));
            Check(Mote5() == "5:11", "Toggle: " + Mote5());
            Check(Marks() == "7:" + Tag(0) + " [" + Tag(1) + "] " + Tag(2), "the selected mark: " + Marks());
            fields.Toggle("Mote", Id(1));
            Check(Mote5() == "", "Toggle did not clear: " + Mote5());
            fields.Select("Depot.Motes[2].Hp");
            Check(Mote5() == "5:12", "Select: " + Mote5());
            Check(fields.For(decls, 8, 8)[0].History == null, "a string has a history");
            fields.Select("Depot.Ticks");
            Check(Mote5() == "5:12", "Select of a static changed the instance");
            return;
        }
        // 次に探し直した後 (探し直すのは 15 frame ごと): 同じ個体を指し続ける
        if (stage == 1 && frames == seenAt + 16)
        {
            Check(Mote5() == "5:12", "the selection did not follow the instance: " + Mote5());
            Check(Marks() == "7:" + Tag(0) + " [" + Tag(1) + "]", "the marks after the shift: " + Marks());
            // 並びがずれる前の frame も、前の [1] (Hp 11) でなく同じ個体 (Hp 12) の値
            // 列は、出し始めてからゲームが進んだ frame ごとに 1 つ
            var sampled = GameApp.Frames - firstGame + 1;
            var moved = fields.For(decls, 5, 5)[0].History;
            Check(moved != null && moved.Count == sampled, "the history of the instance has " + (moved != null ? moved.Count : -1) + " values, want " + sampled);
            if (moved != null)
                foreach (var hp in moved)
                    Check(hp == 12, "the history read another instance: " + hp);
            // Ticks は毎 frame 1 増えるので、最初に見た値から今の値まで続く
            var ticks = shown[1].History;
            Check(ticks != null && ticks.Count == sampled && ticks[0] == firstTicks, "the history of Ticks has " + (ticks != null ? ticks.Count : -1) + " values, want " + sampled + " from " + firstTicks);
            CheckTicks(shown[1]);
            Next();
        }
        // 選んだ個体 (Hp 12) をゲームが外すと、残るのは 1 つ
        else if (stage == 2 && Waited(1))
        {
            Check(Mote5() == "5:11" && Marks() == "", "the only instance left is not shown plainly: " + Mote5() + " " + Marks());
            Next();
        }
        // ゲームが個体を足しても、選択は別の個体へ移らない
        else if (stage == 3 && Waited(2))
        {
            Check(Mote5() == "" && Marks() == "7:" + Tag(0) + " " + Tag(1), "the selection moved to another instance: " + Mote5() + " " + Marks());
            Lider.Work!.FocusOn(Environment.GetEnvironmentVariable("LIDER_ROOT") + "/game/Game.cs");
            Next();
            seenAt = frames;
        }
        // lider が窓を置いた後に、窓が広がり終わるまで 1 秒分進める (印を押す位置は、広がり終わった窓で求める)
        else if (stage == 4 && frames == seenAt + 2)
        {
            for (var i = 0; i < 60; i++)
                Lider.Animate(Lider.Work!, 1.0f / 60);
        }
        else if (stage == 4 && frames == seenAt + 17)
        {
            PressMark();
            Next();
        }
        // ゲームが Calm を変えたのを見た frame
        else if (stage == 5 && shown[0].Text == "1")
        {
            Check(shown[0].Glow > 0.8f, "a value that changed after a while does not glow: " + shown[0].Glow);
            Check(shown[1].Glow == 0, "a value that changes every frame glows");
            Check(shown[2].Glow == 0, "a value that did not change glows");
            // 持つ値は TraceLog.Fade 個まで
            Check(shown[1].History != null && shown[1].History!.Count == TraceLog.Fade, "the history of Ticks is not capped at " + TraceLog.Fade);
            CheckTicks(shown[1]);
            Next();
        }
        else if (stage == 6 && Waited(6))
        {
            Check(Marks() == "7:" + Tag(0) + " " + Tag(1) + " " + Tag(2) + " " + Tag(3) + " … 全 6 個", "over 4: " + Marks());
            fields.Toggle("Mote", Id(3));
            fields.NextPage("Mote");
            Check(Marks() == "7:" + Tag(4) + " [" + Tag(3) + "] … 全 6 個", "the next page: " + Marks());
            fields.NextPage("Mote");
            Check(Marks() == "7:" + Tag(0) + " " + Tag(1) + " " + Tag(2) + " [" + Tag(3) + "] … 全 6 個", "the page did not wrap: " + Marks());
            if (!failed)
            {
                Console.WriteLine("PASS");
                App.Quit(0);
            }
        }
    }

    // 次の段へ
    static void Next()
    {
        stage++;
        seenAt = 0;
    }

    // ゲームの個体が count 個になったのを見てから、次に探し直した後 (16 frame 後) か
    static bool Waited(int count)
    {
        if (seenAt > 0)
            return frames == seenAt + 16;
        if (Count() == count)
            seenAt = frames;
        return false;
    }

    // Ticks の列は 1 ずつ増えて、今の値で終わる
    static void CheckTicks(FieldValue ticks)
    {
        var h = ticks.History;
        if (h == null || h.Count == 0)
        {
            Check(false, "Ticks has no history");
            return;
        }
        Check(h[h.Count - 1] == float.Parse(ticks.Text), "the history of Ticks ends at " + h[h.Count - 1] + ", shown " + ticks.Text);
        for (var i = 1; i < h.Count; i++)
            Check(h[i] == h[i - 1] + 1, "the history of Ticks is not consecutive at " + i + ": " + h[i - 1] + " " + h[i]);
    }

    // ゲームの Depot.Motes の個体の数
    static int Count()
    {
        return GameApp.Inspect("Depot.Motes", 0, 100).Count;
    }

    // lider の窓の class Mote の行の最初の印を押す
    static void PressMark()
    {
        var w = Lider.Work!;
        var window = w.FocusWindow!;
        var panel = Lider.Panel(window);
        FieldMark? mark = null;
        var row = 0;
        foreach (var f in panel.Fields)
            if (f.Class == "Mote" && f.Marks.Count > 0)
            {
                mark = f.Marks[0];
                row = f.Line;
            }
        Check(mark != null && mark.Text == Tag(0), "the window does not show the mark");
        if (mark == null)
            return;
        Check(mark.X1 > mark.X0, "the mark has no place");
        var px = (mark.X0 + mark.X1) * 0.5f;
        var py = EditorHost.LineY(window.ViewId, row) + EditorHost.LineHeight(window.ViewId) * 0.5f;
        Check(panel.MarkAt(px, py) == mark, "MarkAt does not find the mark");
        var a = Lider.WindowAngle(w, window) + (px / PanelView.BasePxW - 0.5f) * PanelView.BaseW * window.Scale / Space.R;
        var y = (0.5f - py / PanelView.BasePxH) * PanelView.BaseH * window.Scale;
        var line = window.Doc.Line;
        Lider.Click(w, new Ray(new Vec3(0, 0, 0), Space.At(a, y, Space.R)), false);
        Check(window.Doc.Line == line, "pressing the mark moved the cursor");
        Check(Lider.Fields.IsSelected("Mote", Id(0)), "pressing the mark did not select the instance");
        Lider.Fields.Toggle("Mote", Id(0));
    }

    static string Mote5()
    {
        return Texts(fields.For(decls, 5, 5));
    }

    static string Id(int at)
    {
        return GameApp.Inspect("Depot.Motes", at, 1)[0].Id;
    }

    static string Tag(int at)
    {
        return FieldValues.Tag(Id(at));
    }

    static string Marks()
    {
        return Texts(fields.For(decls, 7, 7));
    }

    static string Texts(List<FieldValue> list)
    {
        var s = "";
        foreach (var f in list)
        {
            var marks = "";
            foreach (var m in f.Marks)
                marks += (marks == "" ? "" : " ") + (m.Selected ? "[" + m.Text + "]" : m.Text);
            s += (s == "" ? "" : " ") + f.Line + ":" + f.Text + marks;
        }
        return s;
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
