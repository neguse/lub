// 行が層を移るとき (打って手前へ浮く、reload が当たって面へ沈む) は、瞬間に切り替えずに PanelView.MoveSeconds かけて移る。
//   - 打った行は手前へ移り始め、MoveSeconds で移り終える
//   - 保存して reload が当たると、その行は面へ移り始め、移り終えた frame の Step が沈み終えた行の数を返す
//   - 描いたことのない行 (初めて描いたとき、scroll で出てきたとき) は移らない
using System;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestLayers
{
    static int frames;
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
        App.Config(new ConfigOpts { Width = 320, Height = 180 });
    }

    static void TestFrame(float dt)
    {
        frames++;
        if (failed || frames != 3)
            return;
        Lider.SetActive(false);
        var text = new MonoText("test_layers_mono", (Environment.GetEnvironmentVariable("LIDER_HOME") ?? ".") + "/data/yutapon_coding_081d.ttf", 15);
        Document.Boot(text.CellW, text.LineH);
        var doc = Document.Open(Environment.GetEnvironmentVariable("LIDER_TEST_FILE")!)!;
        var log = new TraceLog();
        var panel = new PanelView("test_layers", doc.Id);
        panel.Prepare(doc, text, log);
        Check(!panel.Moving(5), "a line moved on the first draw");

        doc.MoveTo(4, Utf8.Bytes(doc.Text(4)));
        doc.Type("x");
        EditorHost.Step();
        panel.Prepare(doc, text, log);
        Check(panel.Moving(5) && panel.MovingToFront(5), "the typed line 5 did not start to float");
        Check(!panel.Moving(4), "an untouched line moved");
        Check(panel.Step(PanelView.MoveSeconds * 0.5f) == 0 && panel.Moving(5), "the floating line stopped halfway");
        Check(panel.Step(PanelView.MoveSeconds) == 0 && !panel.Moving(5), "the floating line did not arrive or counted as sunk");

        doc.Save();
        doc.Reloaded();
        panel.Prepare(doc, text, log);
        Check(panel.Moving(5) && !panel.MovingToFront(5), "the reloaded line 5 did not start to sink");
        var sank = panel.Step(PanelView.MoveSeconds + 0.01f);
        Check(sank == 1 && !panel.Moving(5), "sinking ended with " + sank + " lines, want 1");
        Check(panel.SankAt > EditorHost.LineY(doc.Id, 5) && panel.SankAt < EditorHost.LineY(doc.Id, 6),
            "the sunk lines are at y " + panel.SankAt + ", not on line 5");
        if (failed)
            return;
        Console.WriteLine("PASS");
        App.Quit(0);
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
