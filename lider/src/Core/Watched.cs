// The game lider is watching: the saved version (GameApp) or the version being edited (DraftGame).
// Game values, execution records, load, and objects drawn in the picture are read through here (returns empty before opening).
using System.Collections.Generic;
using Lub;

namespace LiderCore;

public static class Watched
{
    // The version being watched
    public static GameVersion Version = GameVersion.Saved;
    // Number of times the watched version was switched (used to recreate per-version state when it switches)
    public static int Switches;

    static AppRef? Ref => Version == GameVersion.Draft ? DraftGame.Ref : GameApp.Ref;
    static InputMap Map => Version == GameVersion.Draft ? DraftGame.Map : GameApp.Map;

    // Switches the watched version. Makes the previous version stop counting per-line load
    public static void Watch(GameVersion version)
    {
        if (version == Version)
            return;
        ProfileLines(false);
        Version = version;
        Switches++;
    }

    // While paused, updates the value at path (Inspect's Path) to the value in text (Sys.InspectSet).
    // For the saved version, writes to the state before the paused frame is advanced and recreates that frame (discarding the recorded frames after it).
    // The version being edited does not advance while paused, so it writes to the current state.
    // Returns false if it cannot write; the value stays unchanged and the reason goes into error
    public static bool Set(string path, string text, out string? error)
    {
        error = "ゲームを止めている間だけ書けます";
        var app = Ref;
        if (app == null || GameApp.Position < 0)
            return false;
        if (Version == GameVersion.Draft)
            return Sys.InspectSet(path, text, out error, app);
        string? reason = null;
        var ok = GameApp.Edit(() => Sys.InspectSet(path, text, out reason, app));
        error = ok ? null : reason ?? "止めた frame の状態を読み書きできません";
        return ok;
    }

    // Writes the state of the watched version to save slot number slot (App.Save). Returns false if it cannot write, with the reason in error
    public static bool Save(int slot, out string? error)
    {
        error = "ゲームがまだ動いていない";
        return Ref != null && App.Save(Ref, slot, out error);
    }

    // Loads save slot number slot into the watched version (App.Load). Returns false if it cannot load; the state stays unchanged and the reason goes into error.
    // If loaded while paused, discards the recorded frames and continues from the loaded state
    public static bool Load(int slot, out string? error)
    {
        error = "ゲームがまだ動いていない";
        if (Ref == null || !App.Load(Ref, slot, out error))
            return false;
        if (Version == GameVersion.Saved && GameApp.Position >= 0)
            GameApp.Forget();
        return true;
    }

    // Finds what is drawn at position uv in the picture (top-left is 0,0, bottom-right is 1,1) (the result is PickResult)
    public static void Pick(Vec2 uv)
    {
        if (Ref != null)
            Gfx.Pick(Gfx.MainTex!, (int)(uv.X * Map.W), (int)(uv.Y * Map.H), Ref);
    }

    public static PickHit? PickResult()
    {
        return Ref != null ? Gfx.PickResult(Ref) : null;
    }

    public static List<InspectNode> Inspect(string path, int offset, int count)
    {
        return Ref != null ? Sys.Inspect(path, offset, count, Ref) : new List<InspectNode>();
    }

    public static List<InspectInstance> InspectInstances(int maxDepth, int maxChildren)
    {
        return Ref != null ? Sys.InspectInstances(maxDepth, maxChildren, Ref) : new List<InspectInstance>();
    }

    public static void InspectPin(string key, string path)
    {
        if (Ref != null)
            Sys.InspectPin(key, path, Ref);
    }

    public static bool InspectPinned(string key, string id)
    {
        return Ref != null && Sys.InspectPinned(key, id, Ref);
    }

    public static List<string> TakeTraceFor(string key)
    {
        return Ref != null ? Sys.TakeTraceFor(key, Ref) : new List<string>();
    }

    public static List<TraceValue> TakeValues()
    {
        return Ref != null ? Sys.TakeValues(Ref) : new List<TraceValue>();
    }

    public static void ProfileLines(bool on)
    {
        if (Ref != null)
            Sys.ProfileLines(on, Ref);
    }

    public static List<ProfileLine> TakeProfile()
    {
        return Ref != null ? Sys.TakeProfile(Ref) : new List<ProfileLine>();
    }

    public static List<GcPause> TakeGcPauses()
    {
        return Ref != null ? Sys.TakeGcPauses(Ref) : new List<GcPause>();
    }

    public static List<KeptObject> TakeKept()
    {
        return Ref != null ? Sys.TakeKept(Ref) : new List<KeptObject>();
    }

    public static List<GpuPass> TakeGpuPasses()
    {
        return Ref != null ? Sys.TakeGpuPasses(Ref) : new List<GpuPass>();
    }
}
