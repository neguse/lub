// lider が見ているゲーム: 保存した版 (GameApp) か、編集中の版 (DraftGame)。
// ゲームの値、実行の記録、負荷、絵に描いた物は、ここを通して読む (開く前は空を返す)。
using System.Collections.Generic;
using Lub;

namespace LiderCore;

public static class Watched
{
    // 見ている版
    public static GameVersion Version = GameVersion.Saved;
    // 見る版を替えた回数 (版ごとに持つ状態を、替わったときに作り直すのに使う)
    public static int Switches;

    static AppRef? Ref => Version == GameVersion.Draft ? DraftGame.Ref : GameApp.Ref;
    static InputMap Map => Version == GameVersion.Draft ? DraftGame.Map : GameApp.Map;

    // 見る版を替える。前の版には、行ごとの負荷を数えるのをやめさせる
    public static void Watch(GameVersion version)
    {
        if (version == Version)
            return;
        ProfileLines(false);
        Version = version;
        Switches++;
    }

    // 止めている間に、path の値 (Inspect の Path) を text の値に書き換える (Sys.InspectSet)。
    // 保存した版は、止めた frame を進める前の状態に書き、その frame を作り直す (それより後の控えた frame は捨てる)。
    // 編集中の版は、止めている間は進めないので、今の状態に書く。
    // 書けなければ false で、値は変わらず、理由が error に入る
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

    // 見ている版の状態を、番号 slot の控えに書く (App.Save)。書けなければ false で、理由が error に入る
    public static bool Save(int slot, out string? error)
    {
        error = "ゲームがまだ動いていない";
        return Ref != null && App.Save(Ref, slot, out error);
    }

    // 番号 slot の控えを、見ている版に読み込む (App.Load)。読み込めなければ false で、状態は変わらず、理由が error に入る。
    // 止めている間に読み込んだら、控えた frame を捨てて、読み込んだ状態から続ける
    public static bool Load(int slot, out string? error)
    {
        error = "ゲームがまだ動いていない";
        if (Ref == null || !App.Load(Ref, slot, out error))
            return false;
        if (Version == GameVersion.Saved && GameApp.Position >= 0)
            GameApp.Forget();
        return true;
    }

    // 絵の中の位置 uv (左上が 0,0、右下が 1,1) に何が描いてあるかを調べる (結果は PickResult)
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
