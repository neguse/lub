// Motion for operation feedback.
//   - Spring settles on the target in 1 second and overshoots a little (3% to 15%)
//   - An opened window starts out looking small and reaches the window size in 1 second
//   - The cursor is not drawn into the window texture but drawn separately. When moved, it slides instead of jumping and settles in 0.3 seconds
//   - Typed characters become a Landing and settle onto the surface in Landing.Seconds. Deleting does not create a Landing
//   - When characters disappear from the text (Backspace, typing over a selected range), particles scatter. A Backspace that deletes nothing does not scatter them
//   - Ease.Toward reaches the target in seconds, and is partway there before that
//   - While resizing with Alt, the window floats the same as when grabbed, and comes back when stopped
//   - A closed window fades out in WindowMotion.CloseSeconds
//   - Particles disappear at the end of their lifetime
using System;
using Lub;
using LiderCore;
using LiderEditor;

public static class TestMotion
{
    static bool failed;
    static int frames;
    static int stage;
    static Window? window;

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

    // Wait 1 frame per stage and check after lider has redrawn
    static void TestFrame(float dt)
    {
        // After lider has drawn 1 frame
        frames++;
        if (failed || frames < 3)
            return;
        if (stage == 0)
            Open();
        else if (stage == 1)
            CaretShown();
        else
            Rest();
        stage++;
    }

    static void Open()
    {
        var s = new Spring(0);
        var peak = 0.0f;
        for (var i = 0; i < 60; i++)
        {
            s.Step(1, 1.0f / 60);
            peak = Math.Max(peak, s.Value);
        }
        Check(Math.Abs(s.Value - 1) < 0.02f, "the spring did not settle in 1 s: " + s.Value);
        Check(peak > 1.03f && peak < 1.15f, "the spring overshoot was " + peak);
        var e = Ease.Toward(0, 1, 1.0f / 60, 0.3f);
        Check(e > 0.01f && e < 0.2f, "Ease.Toward took one frame to " + e);
        for (var i = 0; i < 18; i++)
            e = Ease.Toward(e, 1, 1.0f / 60, 0.3f);
        Check(e == 1, "Ease.Toward did not arrive in 0.3 s: " + e);

        var target = Environment.GetEnvironmentVariable("LIDER_TEST_FILE");
        var w = Lider.Work;
        Check(target != null && w != null, "LIDER_TEST_FILE or the workspace is missing");
        if (failed)
            return;
        w!.FocusOn(target!);
        window = w.FocusWindow!;
        Lider.Animate(w, 1.0f / 60);
        var m = Lider.MotionOf(window);
        Check(m != null && m.Scale.Value < 0.5f, "an opened window did not start small: " + (m == null ? -1 : m.Scale.Value));
        for (var i = 0; i < 60; i++)
            Lider.Animate(w, 1.0f / 60);
        Check(m != null && Math.Abs(m.Scale.Value - window.Scale) < 0.03f, "the window did not grow to its size: " + (m == null ? -1 : m.Scale.Value));
        EditorHost.SetCursor(window.Code.ViewId, 1, 1);
    }

    static void CaretShown()
    {
        var id = window!.Code.ViewId;
        var n = EditorHost.Draw(id, PanelView.BasePxW, PanelView.BasePxH);
        Check(EditorHost.CaretCount(id) == 1, "the view has " + EditorHost.CaretCount(id) + " carets, want 1");
        var x = EditorHost.CaretX(id, 0);
        Check(Math.Abs(x - EditorHost.TextX(id)) < 0.5f, "the caret at column 1 is at x " + x + ", want " + EditorHost.TextX(id));
        for (var i = 0; i < n; i++)
            Check(!(EditorHost.CmdKind(i) == 1 && EditorHost.CmdX(i) == x && EditorHost.CmdW(i) == EditorHost.CaretWidth()),
                "the caret was drawn into the window texture");
        var m = Lider.MotionOf(window)!;
        var y = EditorHost.CaretY(id, 0) + EditorHost.ScrollY(id);
        Check(Math.Abs(m.CaretY.Value - y) < 0.5f, "the shown caret is at " + m.CaretY.Value + ", want " + y);
        var fromY = m.CaretY.Value;
        // After advancing by only 1/60 second, it is still partway there
        EditorHost.SetCursor(id, 5, 1);
        EditorHost.Draw(id, PanelView.BasePxW, PanelView.BasePxH);
        var to = EditorHost.CaretY(id, 0) + EditorHost.ScrollY(id);
        Check(to > fromY + 1, "the caret on line 5 is not below line 1: " + to + " vs " + fromY);
        Lider.Animate(Lider.Work!, 1.0f / 60);
        Check(m.CaretY.Value > fromY + 0.5f && m.CaretY.Value < to - 0.5f,
            "the caret jumped: " + fromY + " -> " + m.CaretY.Value + " (target " + to + ")");
        for (var i = 0; i < 18; i++)
            Lider.Animate(Lider.Work!, 1.0f / 60);
        Check(Math.Abs(m.CaretY.Value - to) < 0.5f, "the caret did not settle in 0.3 s: " + m.CaretY.Value + " vs " + to);
        Typed(id);
    }

    // Advance by only 1/60 second right after typing and redrawing (the dt between frames can be long)
    static void Typed(int id)
    {
        EditorHost.Text("ab");
        EditorHost.Draw(id, PanelView.BasePxW, PanelView.BasePxH);
        Lider.Animate(Lider.Work!, 1.0f / 60);
        Check(Lider.Landings.Count == 1, "typing 2 letters made " + Lider.Landings.Count + " landings, want 1");
        if (failed)
            return;
        var l = Lider.Landings[0];
        Check(l.Window == window && l.Line == 5 && l.X1 > l.X0, "the landing is at line " + l.Line + " x " + l.X0 + ".." + l.X1);
        for (var t = 0.0f; t < Landing.Seconds + 0.05f; t += 1.0f / 60)
            Lider.Animate(Lider.Work!, 1.0f / 60);
        Check(Lider.Landings.Count == 0, "a landing outlived " + Landing.Seconds + " s");
        var before = Lider.ParticleCount;
        EditorHost.Key(42, true);
        EditorHost.Key(42, false);
        EditorHost.Draw(id, PanelView.BasePxW, PanelView.BasePxH);
        Lider.Animate(Lider.Work!, 1.0f / 60);
        Check(Lider.Landings.Count == 0, "deleting made a landing");
        Check(Lider.ParticleCount > before, "deleting a letter scattered no particles");
        Check(EditorHost.Line(id, 5).StartsWith("a") && !EditorHost.Line(id, 5).StartsWith("ab"),
            "backspace did not delete: " + EditorHost.Line(id, 5));

        // Backspace at the start deletes nothing
        for (var t = 0.0f; t < 1; t += 1.0f / 60)
            Lider.Animate(Lider.Work!, 1.0f / 60);
        EditorHost.SetCursor(id, 1, 1);
        before = Lider.ParticleCount;
        EditorHost.Key(42, true);
        EditorHost.Key(42, false);
        Lider.Animate(Lider.Work!, 1.0f / 60);
        Check(Lider.ParticleCount == 0 && before == 0, "a backspace that deleted nothing scattered particles");

        // Typing over a selected range (shift + right) scatters particles from where the text disappeared
        EditorHost.SetCursor(id, 5, 1);
        EditorHost.Key(225, true);
        EditorHost.Key(79, true);
        EditorHost.Key(79, false);
        EditorHost.Key(225, false);
        EditorHost.Text("z");
        EditorHost.Draw(id, PanelView.BasePxW, PanelView.BasePxH);
        Lider.Animate(Lider.Work!, 1.0f / 60);
        Check(EditorHost.Line(id, 5).StartsWith("z "), "typing over the selection made: " + EditorHost.Line(id, 5));
        Check(Lider.ParticleCount > 0, "typing over a selection scattered no particles");
    }

    static void Rest()
    {
        var w = Lider.Work!;
        w.FocusWindow = window;
        var m = Lider.MotionOf(window!)!;
        for (var i = 0; i < 10; i++)
        {
            Lider.Run("window:shrink");
            Lider.Animate(w, 1.0f / 60);
        }
        Check(m.Z.Value < -0.3f * Grab.LiftDistance, "resizing a window with Alt did not lift it: " + m.Z.Value);
        for (var i = 0; i < 60; i++)
            Lider.Animate(w, 1.0f / 60);
        Check(m.Z.Value > -0.05f * Grab.LiftDistance, "the window stayed lifted after the keys stopped: " + m.Z.Value);
        w.CloseTab();
        Lider.Animate(w, 1.0f / 60);
        Check(Lider.MotionOf(window!) != null, "a closed window vanished at once");
        for (var t = 0.0f; t < WindowMotion.CloseSeconds + 0.05f; t += 1.0f / 60)
            Lider.Animate(w, 1.0f / 60);
        Check(Lider.MotionOf(window!) == null, "a closed window was still drawn");

        var p = new Particles();
        p.Burst(0, 0, Space.R, 10, 0.3f, -0.3f, Lubx.Color.Rgb(1, 1, 1, 1), 0.5f);
        Check(p.Live.Count == 10, "the burst made " + p.Live.Count + " particles");
        for (var i = 0; i < 40; i++)
            p.Step(1.0f / 60);
        Check(p.Live.Count == 0, p.Live.Count + " particles outlived their life");
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
