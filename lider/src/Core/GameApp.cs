// The saved version of the game lider opens and runs (lub's App.Use). The edited version is DraftGame.
// The game runs in a VM separate from lider and draws to a texture the same size as the window showing the game picture (GameView).
// lider copies that texture into the window (ScreenView) and draws its own things on top.
// Pause, step back and step forward use the state and input recorded per frame (Lubx.AppTimeline).
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

namespace LiderCore;

public static class GameApp
{
    // The game (.csproj, directory, or compiled .tcsb). If empty, nothing is opened
    public static string Project = "";
    // Path of the module (.tcsb) to open. If Project is a .tcsb, that; otherwise what Build compiled. If empty, nothing is opened
    public static string Module = "";
    // Compile of the saved version (no compile if Project is a .tcsb)
    public static GameBuild Build = new GameBuild("");
    // The opened game (null until the first Step)
    public static AppRef? Ref;
    // Texture the game draws to (same size as the window showing the game)
    public static TextureRef? Target;
    // On-screen rectangle of the window showing the game (to correct pick positions) and the mapping from input positions to game pixels
    public static readonly InputMap Map = new InputMap();
    static AppTimeline? timeline;
    // Number of applied reloads, number of recreations, and the diagnostic of the last build (values seen last time; changes are reported in Changed)
    static int seenReloads;
    static int seenRestarts;
    static string seenError = "";
    // Whether the last Step advanced (false if the module could not be loaded or OnFrame failed)
    public static bool Stepped;
    // Number of frames the game advanced (frames spent paused or recreating are not counted)
    public static int Frames;

    // Sets the number of frames to record (0 = no recording). Previous records are discarded
    public static void Keep(int frames)
    {
        timeline?.Clear();
        timeline = frames > 0 ? new AppTimeline(frames) : null;
    }

    // Discards the recorded frames and resumes if paused (call after loading state from outside; if it stays paused, the state is reverted to the paused frame's state)
    public static void Forget()
    {
        timeline?.Clear();
    }

    // Rewrites the state of the paused frame (AppTimeline.Edit). false if not paused
    public static bool Edit(Func<bool> change)
    {
        return timeline != null && Ref != null && timeline.Edit(Ref, change);
    }

    // Number of recorded frames
    public static int Count => timeline != null ? timeline.Count : 0;
    // Paused position (how many frames back; -1 if not paused)
    public static int Position => timeline != null ? timeline.Position : -1;

    // Pauses at the frame back frames ago (does nothing if there is no record)
    public static void Pause(int back)
    {
        timeline?.Pause(back);
    }

    // Resumes from the paused frame
    public static void Resume()
    {
        timeline?.Resume();
    }

    // Recreates the game from scratch (starting from OnInit at the beginning of the next Step)
    public static void Restart()
    {
        if (Ref != null)
            App.Restart(Ref);
    }

    // Texture version. Recreated when the size changes (UseTexture does not change the size if the version is the same)
    public static int SizeVersion(int w, int h)
    {
        return w * 16384 + h;
    }

    // Passes an event to the game's OnEvent. The position is the in-picture position passed earlier with Map.Point, converted to texture pixels (Map)
    public static void Send(EventData e)
    {
        if (Ref != null)
            App.SendEvent(Ref, Map.Event(e));
    }

    // Opens the game (call after setting Project and before Step)
    public static void Open(string project)
    {
        Project = project;
        Build = new GameBuild(project);
        Module = Build.Needed ? "" : project;
    }

    // Advances the game 1 frame.
    // w and h are the picture size in pixels. The mouse position is passed beforehand with Map.Point.
    // keyboard and mouse say whether each is shown to the game
    // Whether it is placed in Space.
    // If it is, the game sees the viewer: during XR the eyes as they are, otherwise lider's camera (SpaceVp), passed as an eye whose render target is the picture (target).
    // If it is not, XR looks inactive to the game, and the game draws to the picture with its own camera
    public static bool InSpace;
    // view-projection of the camera with which lider views Space (same format as Xr.View: row-major, forward -Z; Lider sets it every frame)
    public static float[]? SpaceVp;

    // Writes the viewer shown to the game into input
    public static void ShowViewer(StepInput input, bool inSpace, TextureRef target)
    {
        if (!inSpace)
        {
            input.Xr = false;
            return;
        }
        if (Xr.Active() || SpaceVp == null)
            return;
        // Two eyes are required, so the right eye gets a small throwaway render target
        var spare = Gfx.UseTexture("lider_game_spare_eye", 8, 8, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true });
        if (spare == null)
            return;
        input.XrTargets = new List<TextureRef> { target, spare };
        input.XrViewProjections = new List<float[]> { SpaceVp, SpaceVp };
    }

    public static void Step(float dt, bool keyboard, bool mouse, int w, int h)
    {
        Stepped = false;
        if (Build.Needed)
        {
            Build.Tick(dt);
            Module = Build.ModulePath;
        }
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        Map.Set(w, h);
        Target = Gfx.UseTexture("lider_game", w, h, Gfx.PixelFormat.Rgba8, null, SizeVersion(w, h), new TextureOpts { Target = true });
        if (Module != "" && Target != null)
        {
            Ref = Build.Needed ? App.Use("lider_game_app", Module, Build.Entry) : App.Use("lider_game_app", Module);
            var input = new StepInput { MouseX = Map.X, MouseY = Map.Y };
            input.Keyboard = keyboard;
            input.Mouse = mouse;
            ShowViewer(input, InSpace, Target);
            var paused = Position >= 0;
            Stepped = timeline != null ? timeline.Step(Ref, Target, dt, input) : App.Step(Ref, Target, dt, input);
            if (Stepped && !paused)
                Frames++;
        }
    }

    // What happened to the game since the previous call
    public class Change
    {
        // A reload was applied or the game was recreated (the saved version is running)
        public bool Reloaded;
        // Recreated
        public bool Restarted;
        // Notice about the applied diff (empty if none)
        public string Notes = "";
        // Newly arrived build diagnostic (empty if none)
        public string BuildError = "";
    }

    // Call after Step
    public static Change Changed()
    {
        var change = new Change();
        if (Ref == null)
            return change;
        var status = App.Status(Ref);
        if (status.Reloads != seenReloads)
        {
            change.Reloaded = true;
            change.Notes = status.ReloadNotes;
        }
        if (status.Restarts != seenRestarts)
        {
            change.Reloaded = true;
            change.Restarted = true;
            // The previous VM's state cannot be loaded into a recreated VM
            timeline?.Clear();
        }
        if (status.BuildError != seenError && status.BuildError != "")
            change.BuildError = status.BuildError;
        // Diagnostics from lider's compile and notices about applied diffs (notices are passed on the frame the game read the diff)
        if (Build.PendingErrors != "")
        {
            change.BuildError = Build.PendingErrors;
            Build.PendingErrors = "";
        }
        if (change.Reloaded && Build.PendingNotes != "")
        {
            change.Notes = Build.PendingNotes;
            Build.PendingNotes = "";
        }
        seenReloads = status.Reloads;
        seenRestarts = status.Restarts;
        seenError = status.BuildError;
        return change;
    }

    // Reads values of the saved version (values of the version lider watches are read with Watched)
    public static List<InspectNode> Inspect(string path, int offset, int count)
    {
        return Ref != null ? Sys.Inspect(path, offset, count, Ref) : new List<InspectNode>();
    }
}
