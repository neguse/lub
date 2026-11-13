// インスペクタ: class の下に、宣言の順にフィールド (値) とメソッド (実行の印) が並ぶ。class はゲーム (game/Game.cs) の物。
//   - 起動したときから出ている
//   - 通っているメソッドは光り、通っていないメソッドは光らないが行は残る。しばらく通らないと消える
//   - 行の → を押すと宣言へ飛び、Ctrl+- で戻る
//   - 行の ≫ で絞り込む: class の行は class に、instance の行は個体に、子のある節はその節に。パンくずを押すとその段に戻る
//   - 個体に絞ると、エディタでもその個体を選ぶ。エディタの class の行の個体の目印を押すと、インスペクタもその個体に絞る
//   - 個体に絞ると、その個体の下にメソッドも並び、その個体で通ったメソッドだけが光る
//   - 絞り込んだ個体が辿れなくなると、class に戻る
// ゲームは決めた frame で最初の Crate を外し、その後の決めた frame から Tick を通らない。どちらもゲームの値を見て待つ。
// frame はゲームが動き始めてから数える。
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestInspector
{
    static int frames;
    static bool failed;
    static int removedAt;
    static int stopAt;
    static string first = "";

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
        // ゲームの build を待つ
        if (GameApp.Frames == 0)
            return;
        frames++;
        if (failed || frames < 20)
            return;
        var w = Lider.Work!;
        var view = Lider.Inspector;
        var tree = view.Tree;
        if (frames == 20)
        {
            Check(view.Shown, "the inspector is not shown at the start");
            tree.Toggle("Busy");
            return;
        }
        if (frames == 22)
        {
            Check(Keys(tree, 1, "Busy.") == "Busy.Count Busy.Stop Busy.Crates Busy.Tick() Busy.Never()", "the members of Busy are " + Keys(tree, 1, "Busy."));
            Check(Row(tree, "Busy.Tick()")!.Method && Row(tree, "Busy.Tick()")!.Glow > 0.5f, "the running method does not glow");
            Check(Row(tree, "Busy.Never()")!.Glow == 0, "the method that never ran glows");
            Check(Row(tree, "Busy.Tick()")!.Target == "Busy.Tick", "the method row goes to " + Row(tree, "Busy.Tick()")!.Target);
            if (failed)
                return;
            // → を押すと宣言へ飛ぶ
            w.FocusOn(Environment.GetEnvironmentVariable("LIDER_ROOT") + "/Target.cs");
            var before = w.Focus;
            Press(w, view, tree.Rows.IndexOf(Row(tree, "Busy.Tick()")!), 1 - 0.5f * 32 / InspectView.PxW);
            Check(w.Focus != before && w.Focus!.Path.EndsWith("game/Game.cs") && w.Focus.Text(w.Focus.Line).Contains("void Tick"), "pressing → did not jump to the method");
            w.Back();
            Check(w.Focus == before, "going back did not return");
            // ≫ で class に絞る
            Press(w, view, tree.Rows.IndexOf(Row(tree, "Busy")!), 1 - 1.5f * 32 / InspectView.PxW);
            Check(string.Join("|", tree.Crumbs()) == "全体|Busy", "the crumbs are " + string.Join("|", tree.Crumbs()));
            return;
        }
        if (frames == 24)
        {
            Check(Keys(tree, 0, "") == "Busy.Count Busy.Stop Busy.Crates Busy.Tick() Busy.Never()", "the class scope lists " + Keys(tree, 0, ""));
            // 節に絞る
            Lider.EnterScope(Row(tree, "Busy.Crates")!);
            return;
        }
        if (frames == 26)
        {
            Check(string.Join("|", tree.Crumbs()) == "全体|Busy|Crates", "the node crumbs are " + string.Join("|", tree.Crumbs()));
            var crate = Row(tree, "Busy.Crates[0]");
            Check(crate != null && crate.Depth == 0 && crate.Id != "", "the node scope does not list the crates");
            if (failed)
                return;
            // 個体に絞ると、エディタでも選ぶ
            Lider.EnterScope(crate!);
            Check(Lider.Fields.IsSelected("Crate", crate!.Id), "the editor did not select the crate");
            return;
        }
        if (frames == 28)
        {
            var id = tree.ScopeId;
            Check(string.Join("|", tree.Crumbs()) == "全体|Crate|" + FieldValues.Tag(id), "the instance crumbs are " + string.Join("|", tree.Crumbs()));
            Check(Keys(tree, 0, "") == "Busy.Crates[0].Hp Busy.Crates[0].Name Crate.Hit()", "the instance scope lists " + Keys(tree, 0, ""));
            Check(Row(tree, "Crate.Hit()")!.Glow == 0, "Hit glows for the crate that did not run it");
            // パンくずの全体を押す
            var uv = view.CrumbCenter(0);
            Lider.Click(w, RayAt(uv), false);
            Check(tree.ScopeClass == "" && tree.ScopeId == "", "pressing the first crumb did not go back to all");
            // エディタの目印で個体に絞る
            Lider.ToggleInstance("Crate", id);
            Check(tree.ScopeId == id && tree.ScopeClass == "Crate", "the editor mark did not scope the inspector");
            return;
        }
        if (frames == 29)
        {
            // Hit を通る 2 つ目の Crate に絞ると、Hit が光る
            first = tree.ScopeId;
            Lider.ScopeToInstance(GameApp.Inspect("Busy.Crates", 1, 1)[0].Id);
            return;
        }
        if (frames == 32)
        {
            Check(Row(tree, "Crate.Hit()") != null && Row(tree, "Crate.Hit()")!.Glow > 0.5f, "Hit does not glow for the crate that runs it");
            Lider.ScopeToInstance(first);
            return;
        }
        if (frames == 34)
        {
            Check(tree.ScopeId == first, "did not scope back to the first crate");
            Check(GameApp.Inspect("Busy.Crates", 0, 100).Count == 2, "the game removed the first crate too early");
            return;
        }
        if (frames > 34 && removedAt == 0)
        {
            // ゲームが最初の Crate を外すまで待つ
            if (GameApp.Inspect("Busy.Crates", 0, 100).Count == 1)
                removedAt = frames;
            Check(frames < 600, "the game did not remove the first crate");
            return;
        }
        if (removedAt > 0 && frames == removedAt + 20)
        {
            // 絞り込んだ個体が辿れなくなると class に戻る (static から辿り直すのは 15 frame ごと)
            Check(tree.ScopeId == "" && tree.ScopeClass == "Crate", "a lost instance did not go back to the class: " + string.Join("|", tree.Crumbs()));
            tree.ScopeTo(0);
            return;
        }
        if (removedAt > 0 && frames > removedAt + 20 && stopAt == 0)
        {
            // ゲームが Tick を通らなくなるまで待つ
            if (Row(tree, "Busy.Stop") != null && Row(tree, "Busy.Stop")!.Text == "true")
                stopAt = frames;
            Check(frames < 600, "the game did not stop");
            return;
        }
        if (stopAt > 0 && frames == stopAt + TraceLog.Fade + 2)
        {
            Check(Row(tree, "Busy.Tick()") != null && Row(tree, "Busy.Tick()")!.Glow == 0, "the method still glows after it stopped");
            if (failed)
                return;
            Console.WriteLine("PASS");
            App.Quit(0);
        }
    }

    // depth の行のうち key が prefix で始まる物の key
    static string Keys(InspectTree tree, int depth, string prefix)
    {
        var keys = new List<string>();
        foreach (var r in tree.Rows)
            if (r.Depth == depth && r.Key.StartsWith(prefix))
                keys.Add(r.Key);
        return string.Join(" ", keys);
    }

    static InspectRow? Row(InspectTree tree, string key)
    {
        foreach (var r in tree.Rows)
            if (r.Key == key)
                return r;
        return null;
    }

    // 板の i 番目の行の、横 u の所を押す
    static void Press(Workspace w, InspectView view, int i, float u)
    {
        var lineH = EditorHost.LineHeight(w.FocusWindow != null ? w.FocusWindow.Code.ViewId : 0);
        Lider.Click(w, RayAt(new Vec2(u, (view.RowY(i) + lineH * 0.5f) / InspectView.PxH)), false);
    }

    // 板の中の位置を指す ray
    static Ray RayAt(Vec2 uv)
    {
        var scale = Space.Flat ? 1.4f : 1;
        var r = Space.R - 0.12f;
        var angle = Lider.BoardAngle + (uv.X - 0.5f) * InspectView.W * scale / r;
        var y = (0.5f - uv.Y) * InspectView.H * scale;
        return new Ray(new Vec3(0, 0, 0), Space.At(angle, y, r));
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
