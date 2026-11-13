// lider が開いて動かすゲーム (lub の App.Use)。
// ゲームは lider とは別の VM で動き、画面と同じ大きさの texture に描く。lider はそれを画面に写してから、上に自分の物を描く。
// 止める、戻る、進むは、frame ごとに控えた状態と入力 (Lubx.AppTimeline) で行う。
// ゲームの値と実行の記録は、ここを通して読む (開く前は空を返す)。
using System;
using System.Collections.Generic;
using Lub;
using Lubx;

namespace LiderCore;

public static class GameApp
{
    const string Vs = "struct VSOut { float2 uv : TEXCOORD0; float4 pos : SV_Position; };\n"
        + "[shader(\"vertex\")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {\n"
        + "  float2 c = float2(float(vid & 1), float(vid >> 1));\n"
        + "  VSOut o;\n"
        + "  o.pos = float4(c.x * 2.0 - 1.0, 1.0 - c.y * 2.0, 0.0, 1.0);\n"
        + "  o.uv = c;\n"
        + "  return o;\n"
        + "}\n";
    const string Fs = "LUB_TEXTURE2D(tex);\n"
        + "struct FSIn { float2 uv : TEXCOORD0; };\n"
        + "[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target {\n"
        + "  return float4(LUB_SAMPLE(tex, i.uv).rgb, 1.0);\n"
        + "}\n";

    // ゲームの .csproj (web では .tcsb) の path。空なら開かない
    public static string Module = "";
    // 開いたゲーム (最初の Step まで null)
    public static AppRef? Ref;
    // ゲームが描く texture (画面と同じ大きさ)
    public static TextureRef? Target;
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

    // ゲームの OnEvent に event を渡す。位置は画面の座標から texture の画素に直す
    public static void Send(EventData e, float pixelRatio)
    {
        if (Ref == null)
            return;
        if (pixelRatio != 1)
            e = new EventData
            {
                Kind = e.Kind,
                Key = e.Key,
                Mods = e.Mods,
                Typed = e.Typed,
                Plain = e.Plain,
                Button = e.Button,
                X = e.X * pixelRatio,
                Y = e.Y * pixelRatio,
                Dx = e.Dx * pixelRatio,
                Dy = e.Dy * pixelRatio,
            };
        App.SendEvent(Ref, e);
    }

    // ゲームを 1 frame 進め、描いた絵を画面に写す。
    // grabbed なら、ゲームにはキーボードとマウスを見せない。pixelRatio は画面の座標の 1 に対する画面の画素の数
    public static void Step(float dt, bool grabbed, float pixelRatio)
    {
        Stepped = false;
        Gfx.Size(out var sw, out var sh);
        var w = Math.Max(1, (int)(sw * pixelRatio));
        var h = Math.Max(1, (int)(sh * pixelRatio));
        Target = Gfx.UseTexture("lider_game", w, h, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true });
        var shader = Gfx.UseShader("lider_game_blit", Vs, Fs, 1);
        if (Module != "" && Target != null)
        {
            Ref = App.Use("lider_game_app", Module);
            Input.MousePos(out var mx, out var my);
            var input = new StepInput { MouseX = mx * pixelRatio, MouseY = my * pixelRatio };
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
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        if (shader != null && Target != null && Ref != null)
        {
            var bindings = new Bindings();
            bindings.Set("tex", Target);
            Gfx.Draw(4, bindings, new DrawOpts
            {
                Shader = shader,
                Cull = Gfx.Cull.None,
                Primitive = Gfx.Primitive.TriangleStrip,
                Depth = false,
            });
        }
        Gfx.EndPass();
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
        seenReloads = status.Reloads;
        seenRestarts = status.Restarts;
        seenError = status.BuildError;
        return change;
    }

    // 画面の画素 (x, y) に何が描いてあるかを調べる (結果は PickResult)
    public static void Pick(int x, int y)
    {
        if (Ref != null)
            Gfx.Pick(Gfx.MainTex!, x, y, Ref);
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
