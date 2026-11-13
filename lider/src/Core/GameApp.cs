// lider が開いて動かすゲームの、保存した版 (lub の App.Use)。編集中の版は DraftGame。
// ゲームは lider とは別の VM で動き、ゲームの絵を映すウィンドウ (GameView) と同じ大きさの texture に描く。
// lider はその texture をウィンドウに写し (ScreenView)、上に自分の物を描く。
// 止める、戻る、進むは、frame ごとに控えた状態と入力 (Lubx.AppTimeline) で行う。
// ゲームの値と実行の記録は、ここを通して読む (開く前は空を返す)。
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

namespace LiderCore;

public static class GameApp
{
    // ゲーム (.csproj、ディレクトリ、変換済みの .tcsb)。空なら開かない
    public static string Project = "";
    // 開く module (.tcsb) の path。Project が .tcsb ならそれ、ほかは Build が変換した物。空なら開かない
    public static string Module = "";
    // 保存した版の変換 (Project が .tcsb なら変換しない)
    public static GameBuild Build = new GameBuild("");
    // 開いたゲーム (最初の Step まで null)
    public static AppRef? Ref;
    // ゲームが描く texture (ゲームを映すウィンドウと同じ大きさ)
    public static TextureRef? Target;
    // ゲームを映すウィンドウの画面の上の矩形 (pick の位置を直す) と、入力の位置をゲームの画素に直す写し方
    static PixelRect? shownAt;
    public static readonly InputMap Map = new InputMap();
    static AppTimeline? timeline;
    // reload が当たった回数、作り直した回数、最後の build の診断 (前に見た値。変わったら Changed に出す)
    static int seenReloads;
    static int seenRestarts;
    static string seenError = "";
    // 最後の Step が進めたか (module を読めない、OnFrame が失敗した、で false)
    public static bool Stepped;
    // ゲームを進めた frame の数 (止めて作り直している frame は数えない)
    public static int Frames;

    // 控える frame の数を決める (0 で控えない)。前の控えは捨てる
    public static void Keep(int frames)
    {
        timeline?.Clear();
        timeline = frames > 0 ? new AppTimeline(frames) : null;
    }

    // 控えている frame の数
    public static int Count => timeline != null ? timeline.Count : 0;
    // 止めている位置 (何個前か。止めていなければ -1)
    public static int Position => timeline != null ? timeline.Position : -1;

    // back 個前の frame で止める (控えが無ければ何もしない)
    public static void Pause(int back)
    {
        timeline?.Pause(back);
    }

    // 止めた frame から続ける
    public static void Resume()
    {
        timeline?.Resume();
    }

    // ゲームを最初から作り直す (次の Step の始めに OnInit から始める)
    public static void Restart()
    {
        if (Ref != null)
            App.Restart(Ref);
    }

    // texture の version。大きさが変わったら作り直す (UseTexture は version が同じなら大きさを変えない)
    public static int SizeVersion(int w, int h)
    {
        return w * 16384 + h;
    }

    // ゲームの OnEvent に event を渡す。位置は画面の座標から texture の画素に直す (Map)
    public static void Send(EventData e)
    {
        if (Ref != null)
            App.SendEvent(Ref, Map.Event(e));
    }

    // ゲームを開く (Project を決めた後に、Step より前に呼ぶ)
    public static void Open(string project)
    {
        Project = project;
        Build = new GameBuild(project);
        Module = Build.Needed ? "" : project;
    }

    // ゲームを 1 frame 進める。
    // at はゲームを映すウィンドウの画面の上の矩形 (画面の座標)、from は入力の位置を測るウィンドウの矩形 (編集中の版と入力を揃えるとき、マウスのある方)、pixelRatio は画面の座標の 1 に対する画面の画素の数。
    // grabbed なら、ゲームにはキーボードとマウスを見せない
    public static void Step(float dt, bool grabbed, PixelRect at, PixelRect from, float pixelRatio)
    {
        Stepped = false;
        if (Build.Needed)
        {
            Build.Tick(dt);
            Module = Build.ModulePath;
        }
        var w = Math.Max(1, (int)(at.W * pixelRatio));
        var h = Math.Max(1, (int)(at.H * pixelRatio));
        shownAt = at;
        Map.Set(from, w, h);
        Target = Gfx.UseTexture("lider_game", w, h, Gfx.PixelFormat.Rgba8, null, SizeVersion(w, h), new TextureOpts { Target = true });
        if (Module != "" && Target != null)
        {
            Ref = Build.Needed ? App.Use("lider_game_app", Module, Build.Entry) : App.Use("lider_game_app", Module);
            Input.MousePos(out var mx, out var my);
            var input = new StepInput { MouseX = Map.X(mx), MouseY = Map.Y(my) };
            if (grabbed)
            {
                input.Keyboard = false;
                input.Mouse = false;
            }
            var paused = Position >= 0;
            Stepped = timeline != null ? timeline.Step(Ref, Target, dt, input) : App.Step(Ref, Target, dt, input);
            if (Stepped && !paused)
                Frames++;
        }
    }

    // 前に呼んでから、ゲームに起きたこと
    public class Change
    {
        // reload が当たったか、作り直した (保存した版が動いている)
        public bool Reloaded;
        // 作り直した
        public bool Restarted;
        // 当てた差分の知らせ (無ければ空)
        public string Notes = "";
        // 新しく届いた build の診断 (無ければ空)
        public string BuildError = "";
    }

    // Step の後に呼ぶ
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
            // 作り直した VM に、前の VM の状態は読み込めない
            timeline?.Clear();
        }
        if (status.BuildError != seenError && status.BuildError != "")
            change.BuildError = status.BuildError;
        // lider が変換したときの診断と、当てた差分の知らせ (知らせは、ゲームが差分を読んだ frame に渡す)
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

    // 画面の座標 (x, y) に何が描いてあるかを調べる (結果は PickResult)。ゲームを映すウィンドウの外なら調べない
    public static void Pick(float x, float y)
    {
        if (Ref == null || shownAt == null || !shownAt.Contains(x, y))
            return;
        var at = new InputMap();
        at.Set(shownAt, Map.W, Map.H);
        Gfx.Pick(Gfx.MainTex!, (int)at.X(x), (int)at.Y(y), Ref);
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
