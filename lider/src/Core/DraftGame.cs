// The game in its edited version: compiled from the unsaved text of open files (GameBuild's draft) and run next to the saved version (GameApp).
// The text is passed to the compile after typing has paused for Settle seconds.
// When opened and when recreated (the first module and restart), copies the state of the saved version (App.Save and App.Load).
// Paused while the saved version is paused. The value tree and the execution record look only at the saved version.
using System;
using System.Collections.Generic;
using Lub;

namespace LiderCore;

public static class DraftGame
{
    // Seconds after typing stops before the text is passed to the compile
    public const float Settle = 0.3f;
    // Slot number of the saved copy used to copy state (in a range that does not overlap the numbers AppTimeline uses)
    const int StateSlot = 1000000;

    public static GameBuild? Build;
    public static AppRef? Ref;
    public static TextureRef? Target;
    public static readonly InputMap Map = new InputMap();
    // Why the state could not be copied (empty if it was copied)
    public static string Notice = "";
    // Copies the saved version's state before the next Step
    public static bool CopyPending;
    // Number of times the state was copied
    public static int Copies;
    // The text passed to the compile, and per text, the seconds since it last changed
    static readonly Dictionary<string, string> settled = new Dictionary<string, string>();
    static readonly Dictionary<string, string> typing = new Dictionary<string, string>();
    static readonly Dictionary<string, float> quiet = new Dictionary<string, float>();
    static bool running;
    static int seenRestarts;

    public static bool Shown => Build != null;

    // Label of the side-by-side window (also says so if the compile failed)
    public static string Label()
    {
        if (Build == null || Build.LastKind != "error")
            return "編集中";
        return Build.Revision > 0 ? "編集中 (変換エラー: 最後に通った版)" : "編集中 (変換エラー)";
    }

    // Opens the edited version (the same game as the saved version)
    public static void Open(string project)
    {
        Build = new GameBuild(project, "draft");
        settled.Clear();
        typing.Clear();
        quiet.Clear();
        running = false;
        Notice = "";
    }

    // Closes it (it stops declaring, so the app closes on the next frame)
    public static void Close()
    {
        Build = null;
        Ref = null;
        Target = null;
        Notice = "";
    }

    // Advances 1 frame. docs are the open files (their unsaved text is used).
    // Does not advance if paused (the saved version is paused); w, h, keyboard and mouse are the same as GameApp.Step
    // Whether it is placed in Space (same as GameApp.InSpace)
    public static bool InSpace;

    public static void Step(float dt, bool keyboard, bool mouse, bool paused, int w, int h, Dictionary<string, Document> docs)
    {
        if (Build == null)
            return;
        Settled(dt, docs);
        var unsaved = new Dictionary<string, string>();
        foreach (var pair in settled)
            unsaved[pair.Key] = pair.Value;
        Build.Unsaved = unsaved;
        Build.Tick(dt);
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        Map.Set(w, h);
        Target = Gfx.UseTexture("lider_draft", w, h, Gfx.PixelFormat.Rgba8, null, GameApp.SizeVersion(w, h), new TextureOpts { Target = true });
        if (Build.ModulePath == "" || Target == null)
            return;
        // The module name (<Entry>.draft.tcsb) differs from the entry, so the entry is passed
        Ref = App.Use("lider_draft_app", Build.ModulePath, Build.Entry);
        // The copy happens after the saved version has advanced this frame. The copied version is already at the same frame, so it does not advance this frame
        if (CopyPending && GameApp.Ref != null && running)
        {
            CopyState();
            return;
        }
        if (paused)
            return;
        var input = new StepInput { MouseX = Map.X, MouseY = Map.Y };
        input.Keyboard = keyboard;
        input.Mouse = mouse;
        GameApp.ShowViewer(input, InSpace, Target);
        App.Step(Ref, Target, dt, input);
        // Copies the saved version's state when it first runs and when it is recreated
        var status = App.Status(Ref);
        if ((status.Running && !running) || status.Restarts != seenRestarts)
            CopyPending = true;
        running = status.Running;
        seenRestarts = status.Restarts;
    }

    public static void Send(EventData e)
    {
        if (Ref != null)
            App.SendEvent(Ref, Map.Event(e));
    }

    // Copies the saved version's current state into the edited version. If it cannot, puts the reason in Notice
    static void CopyState()
    {
        CopyPending = false;
        string? error;
        if (!App.Save(GameApp.Ref!, StateSlot, out error) || !App.Load(Ref!, StateSlot, out error))
        {
            Notice = "編集中の版に、保存した版の状態を写せません: " + (error ?? "") + " (編集中の版は最初から動いています)";
            Console.WriteLine("lider: draft state not copied: " + (error ?? ""));
        }
        else
        {
            Notice = "";
            Copies++;
            Console.WriteLine("lider: draft state copied");
        }
        App.Drop(StateSlot);
    }

    // Puts the unsaved texts whose typing has paused for Settle seconds into settled (absolute paths). Saved files are removed from settled
    static void Settled(float dt, Dictionary<string, Document> docs)
    {
        var dirty = new List<string>();
        foreach (var pair in docs)
        {
            var doc = pair.Value;
            if (!doc.Dirty)
                continue;
            var path = Absolute(pair.Key);
            dirty.Add(path);
            var text = string.Join("\n", doc.Lines());
            if (!typing.TryGetValue(path, out var before) || before != text)
            {
                typing[path] = text;
                quiet[path] = 0;
                continue;
            }
            quiet[path] += dt;
            if (quiet[path] >= Settle)
                settled[path] = text;
        }
        foreach (var path in new List<string>(settled.Keys))
            if (!dirty.Contains(path))
            {
                settled.Remove(path);
                typing.Remove(path);
                quiet.Remove(path);
            }
    }

    static string Absolute(string path)
    {
        return Disk.AbsolutePath(path, out var absolute) && absolute != null ? absolute : path;
    }
}
