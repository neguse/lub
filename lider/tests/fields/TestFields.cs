// Current values shown on field declaration lines, and instance markers on class declaration lines (FieldValues). The values and instances belong to the game (game/Game.cs).
//   - A static shows its value. A const is not shown. Lines outside the range are not shown
//   - If there is one instance, its value is shown and no marker is shown
//   - If there are several, markers are lined up on the class line, and nothing is shown on field lines until one is selected. Toggle selects, and toggling again deselects. Select selects the instance at a line's path or at an ancestor
//   - With more than 4, "… 全 N 個" is added, NextPage lists the rest, and the page after the last is the first. The selected instance is included even when outside the range
//   - Even if elements are removed and the order shifts, it keeps pointing at the same instance (the markers and values do not change). It does not flash even if the path changes
//   - When the selected instance can no longer be traced, the selection is cleared and does not move to another instance
//   - A value that has not changed for a while flashes when it changes, and a value that changes every frame does not flash
//   - A numeric field keeps up to TraceLog.Fade values, one per game frame while it is shown. A string field does not keep them. The instance is followed even if the order of elements changes
//   - Markers appear on the class line in the window, and pressing a marker selects the instance
//   - Even if the order shifts before the next search, values of another instance are not shown
// The game changes Depot.Motes and Depot.Calm at fixed frames. The test watches the game's values and checks the next item after it sees the change.
// Visible values lag by 1 frame (lider advances the game after TestFrame).
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
    // The stage being checked, and the frame where counting started for that stage (the frame where the game's change was seen, or where the stage was entered; 0 if not yet)
    static int stage;
    static int seenAt;
    // Depot.Ticks and GameApp.Frames of the first frame
    static float firstTicks;
    static int firstGame;

    public static void OnInit()
    {
        TestInit();
        Lider.OnInit();
        // Check in the overlay layout (code windows lined up in a horizontal row on the cylindrical screen in space)
        Lider.Arrange(Lider.Overlay);
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
        // Wait for the game build and for the game to place the first instance
        if (failed || GameApp.Frames == 0 || (frames == 0 && Count() != 3))
            return;
        frames++;
        if (frames > 900)
        {
            Check(false, "stage " + stage + " did not finish: " + Count() + " motes");
            return;
        }
        // The frame where the order shift was seen is checked before the next search (Refresh)
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
        // After the next search (searched again every 15 frames): it keeps pointing at the same instance
        if (stage == 1 && frames == seenAt + 16)
        {
            Check(Mote5() == "5:12", "the selection did not follow the instance: " + Mote5());
            Check(Marks() == "7:" + Tag(0) + " [" + Tag(1) + "]", "the marks after the shift: " + Marks());
            // The frames before the order shift also hold the values of the same instance (Hp 12), not of the former [1] (Hp 11)
            // The list has one entry per frame the game advanced since showing started
            var sampled = GameApp.Frames - firstGame + 1;
            var moved = fields.For(decls, 5, 5)[0].History;
            Check(moved != null && moved.Count == sampled, "the history of the instance has " + (moved != null ? moved.Count : -1) + " values, want " + sampled);
            if (moved != null)
                foreach (var hp in moved)
                    Check(hp == 12, "the history read another instance: " + hp);
            // Ticks increases by 1 every frame, so the list runs continuously from the first seen value to the current value
            var ticks = shown[1].History;
            Check(ticks != null && ticks.Count == sampled && ticks[0] == firstTicks, "the history of Ticks has " + (ticks != null ? ticks.Count : -1) + " values, want " + sampled + " from " + firstTicks);
            CheckTicks(shown[1]);
            Next();
        }
        // When the game removes the selected instance (Hp 12), one remains
        else if (stage == 2 && Waited(1))
        {
            Check(Mote5() == "5:11" && Marks() == "", "the only instance left is not shown plainly: " + Mote5() + " " + Marks());
            Next();
        }
        // Even if the game adds an instance, the selection does not move to another instance
        else if (stage == 3 && Waited(2))
        {
            Check(Mote5() == "" && Marks() == "7:" + Tag(0) + " " + Tag(1), "the selection moved to another instance: " + Mote5() + " " + Marks());
            Lider.Work!.FocusOn(Environment.GetEnvironmentVariable("LIDER_ROOT") + "/game/Game.cs");
            Next();
            seenAt = frames;
        }
        // After lider places the window, advance 1 second until the window finishes expanding (the position for pressing a marker is computed on the fully expanded window)
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
        // Frame where the game was seen changing Calm
        else if (stage == 5 && shown[0].Text == "1")
        {
            Check(shown[0].Glow > 0.8f, "a value that changed after a while does not glow: " + shown[0].Glow);
            Check(shown[1].Glow == 0, "a value that changes every frame glows");
            Check(shown[2].Glow == 0, "a value that did not change glows");
            // Up to TraceLog.Fade values are kept
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

    // Go to the next stage
    static void Next()
    {
        stage++;
        seenAt = 0;
    }

    // Whether it is after the next search (16 frames later) since the game's instances were seen to reach count
    static bool Waited(int count)
    {
        if (seenAt > 0)
            return frames == seenAt + 16;
        if (Count() == count)
            seenAt = frames;
        return false;
    }

    // The Ticks list increases by 1 at a time and ends at the current value
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

    // Number of instances in the game's Depot.Motes
    static int Count()
    {
        return GameApp.Inspect("Depot.Motes", 0, 100).Count;
    }

    // Press the first marker on the class Mote line of lider's window
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
        var py = EditorHost.LineY(window.Code.ViewId, row) + EditorHost.LineHeight(window.Code.ViewId) * 0.5f;
        Check(panel.MarkAt(px, py) == mark, "MarkAt does not find the mark");
        var a = Lider.WindowAngle(w, window) + (px / PanelView.BasePxW - 0.5f) * PanelView.BaseW * window.Scale / Space.R;
        var y = (0.5f - py / PanelView.BasePxH) * PanelView.BaseH * window.Scale;
        var line = window.Code.Doc.Line;
        Lider.Click(w, new Ray(new Vec3(0, 0, 0), Space.At(a, y, Space.R)), false);
        Check(window.Code.Doc.Line == line, "pressing the mark moved the cursor");
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
