// 編集中の版のゲーム: 開いている file の保存していない本文で変換し (GameBuild の draft)、保存した版 (GameApp) と並べて動かす。
// 本文は、打つのが Settle 秒止まってから変換に渡す。
// 開いた時と、作り直された時 (最初の module と restart) に、保存した版の状態を写す (App.Save と App.Load)。
// 保存した版を止めている間は止める。値の木と実行の記録は保存した版だけを見る。
using System;
using System.Collections.Generic;
using Lub;

namespace LiderCore;

public static class DraftGame
{
    // 打つのが止まってから、本文を変換に渡すまでの秒
    public const float Settle = 0.3f;
    // 状態を写す控えの番号 (AppTimeline が使う番号と重ならない所)
    const int StateSlot = 1000000;

    public static GameBuild? Build;
    public static AppRef? Ref;
    public static TextureRef? Target;
    public static readonly InputMap Map = new InputMap();
    // 状態を写せなかったわけ (写せたら空)
    public static string Notice = "";
    // 次の Step の前に、保存した版の状態を写す
    public static bool CopyPending;
    // 状態を写した回数
    public static int Copies;
    // 変換に渡した本文と、本文ごとの最後に変わった時からの秒
    static readonly Dictionary<string, string> settled = new Dictionary<string, string>();
    static readonly Dictionary<string, string> typing = new Dictionary<string, string>();
    static readonly Dictionary<string, float> quiet = new Dictionary<string, float>();
    static bool running;
    static int seenRestarts;

    public static bool Shown => Build != null;

    // 並べたウィンドウの札 (変換できなければ、そのことも書く)
    public static string Label()
    {
        if (Build == null || Build.LastKind != "error")
            return "編集中";
        return Build.Revision > 0 ? "編集中 (変換エラー: 最後に通った版)" : "編集中 (変換エラー)";
    }

    // 編集中の版を開く (保存した版と同じゲーム)
    public static void Open(string project)
    {
        Build = new GameBuild(project, "draft") { Traced = false };
        settled.Clear();
        typing.Clear();
        quiet.Clear();
        running = false;
        Notice = "";
    }

    // 閉じる (宣言をやめるので、app は次の frame に閉じる)
    public static void Close()
    {
        Build = null;
        Ref = null;
        Target = null;
        Notice = "";
    }

    // 1 frame 進める。docs は開いている file (保存していない本文を使う)。
    // paused なら進めず (保存した版を止めている)、at と from と grabbed は GameApp.Step と同じ
    public static void Step(float dt, bool grabbed, bool paused, Bounds at, Bounds from, float pixelRatio, Dictionary<string, Document> docs)
    {
        if (Build == null)
            return;
        Settled(dt, docs);
        var unsaved = new Dictionary<string, string>();
        foreach (var pair in settled)
            unsaved[pair.Key] = pair.Value;
        Build.Unsaved = unsaved;
        Build.Tick(dt);
        var w = Math.Max(1, (int)(at.W * pixelRatio));
        var h = Math.Max(1, (int)(at.H * pixelRatio));
        Map.Set(from, w, h);
        Target = Gfx.UseTexture("lider_draft", w, h, Gfx.PixelFormat.Rgba8, null, GameApp.SizeVersion(w, h), new TextureOpts { Target = true });
        if (Build.ModulePath == "" || Target == null)
            return;
        // module の名前 (<Entry>.draft.tcsb) は entry と違うので、entry を渡す
        Ref = App.Use("lider_draft_app", Build.ModulePath, Build.Entry);
        // 写すのは、保存した版がこの frame を進めた後。写した版はもう同じ frame にいるので、この frame は進めない
        if (CopyPending && GameApp.Ref != null && running)
        {
            CopyState();
            return;
        }
        if (paused)
            return;
        Input.MousePos(out var mx, out var my);
        var input = new StepInput { MouseX = Map.X(mx), MouseY = Map.Y(my) };
        if (grabbed)
        {
            input.Keyboard = false;
            input.Mouse = false;
        }
        App.Step(Ref, Target, dt, input);
        // 最初に動いた時と、作り直された時に、保存した版の状態を写す
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

    // 保存した版の今の状態を、編集中の版に写す。写せなければ Notice にわけを入れる
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

    // 保存していない本文のうち、打つのが Settle 秒止まった物を settled に入れる (絶対 path)。保存した file は settled から外す
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
