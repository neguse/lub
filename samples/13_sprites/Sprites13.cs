// lub の samples/13_sprites の entry。
// 実行: lub samples/13_sprites/Sprites13.csproj (transpile + watch + hot reload)
// スコアリング (LCG rand01 / spawn 閾値 / SPRITES13_SCORE の書式) は
// docs/sprites-bench.md の契約。tcs 制約による置換: 数値 parse → parseDec (@byte 走査の
// 10 進 leading parse、out は extern stub 専用なので結果は class で返す)、
// tick % n == 0 → everyN (floor)、Std.int → Math.Floor (対象値は常に非負)。
// spriteRects/whiteRect は static 初期化子で作れない (Lua 出力は sample →
// cs-lib の順なので Rect が未定義) ため onInit で作る。

using System;
using System.Collections.Generic;
using static Lub;

/// <summary>parseDec の結果 (tcs では自作関数の out が使えないため class 返し)。</summary>
public class ParsedDec13
{
    public bool Ok = false;
    public float Value = 0.0f;
    public int IntPart = 0;
}

public class BenchSprite13
{
    public float X;
    public float Y;
    public float TimeInit;
    public float TimeLeft;
    public float R;
    public float Dr;
    public float Cr;
    public float Sr;
    public float StepCr;
    public float StepSr;
    public float TintS;
    public float TintC;
    public float TintStepS;
    public float TintStepC;
    public float Scale;
    public int Kind;

    public BenchSprite13(float x, float y, float timeInit, float r,
        float dr, float scale, int kind)
    {
        this.X = x;
        this.Y = y;
        this.TimeInit = timeInit;
        this.TimeLeft = timeInit;
        this.R = r;
        this.Dr = dr;
        this.Cr = (float)Math.Cos(r);
        this.Sr = (float)Math.Sin(r);
        float step = dr / 60.0f;
        this.StepCr = (float)Math.Cos(step);
        this.StepSr = (float)Math.Sin(step);
        float phase = r * 3.0f;
        this.TintS = (float)Math.Sin(phase);
        this.TintC = (float)Math.Cos(phase);
        float tintStep = step * 3.0f;
        this.TintStepS = (float)Math.Sin(tintStep);
        this.TintStepC = (float)Math.Cos(tintStep);
        this.Scale = scale;
        this.Kind = kind;
    }

    public void Update(float dt)
    {
        R = R + dt * Dr;
        TimeLeft = TimeLeft - dt;
        float nextCr = Cr * StepCr - Sr * StepSr;
        Sr = Cr * StepSr + Sr * StepCr;
        Cr = nextCr;
        float nextTintS = TintS * TintStepC + TintC * TintStepS;
        TintC = TintC * TintStepC - TintS * TintStepS;
        TintS = nextTintS;
    }

    public bool Dead()
    {
        return TimeLeft < 0;
    }
}

public static class Sprites13
{
    const int w = 640;
    const int h = 480;
    const float tickDt = 1.0f / 60.0f;
    const int texW = 80;
    const int texH = 16;
    const int cell = 16;
    const float sqrt3Half = 0.8660254037844386f;

    static List<BenchSprite13> sprites = new List<BenchSprite13>();
    static SpriteBatch? batch = null;
    static Atlas? atlas = null;
    static FpsMeter? meter = null;
    static int tick = 0;
    static float rng = 305419896.0f;
    static float timeMultiply = 4.0f;
    static float targetFps = 60.0f;
    static int maxSprites = 200000;
    static int burst = 1;
    static int scoreFrame = 0;
    static bool scorePrinted = false;
    static bool useInstancing = true;
    static FixedStep? step = null;

    static List<Rect> spriteRects = new List<Rect>();
    static Rect? whiteRect = null;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
        Lub.Config(new ConfigOpts { Backend = backend, Width = w, Height = h });

        spriteRects = new List<Rect>
        {
            new Rect(0, 0, cell, cell),
            new Rect(16, 0, cell, cell),
            new Rect(32, 0, cell, cell),
            new Rect(48, 0, cell, cell),
        };
        var wr = new Rect(64, 0, 1, 1);
        whiteRect = wr;

        targetFps = EnvFloat("LUB_SPRITE_TARGET_FPS", 60.0f);
        maxSprites = EnvInt("LUB_SPRITE_MAX", 200000);
        burst = EnvInt("LUB_SPRITE_BURST", 1);
        scoreFrame = EnvInt("LUB_SPRITE_SCORE_FRAME", 0);
        useInstancing = EnvBool("LUB_SPRITE_INSTANCED", true);
        if (burst < 1)
            burst = 1;

        batch = new SpriteBatch(w, h, "sprites13_shader", "sprites13_batch",
            useInstancing);
        atlas = Atlas.FromPixels("sprites13_atlas", texW, texH,
            BuildAtlas(wr), 1,
            new TextureOpts { Filter = Gfx.Filter.Linear, Wrap = Gfx.Wrap.Clamp });
        meter = new FpsMeter(targetFps);
    }

    // Std.parseFloat/parseInt 相当 (読めたかどうかを返すので手書き)。
    // 先頭の [+-]?digits[.digits] を読み、後続の文字は無視する。ok=false は
    // 数字ゼロ個 (parse 失敗)。10 進のみ (hex/指数は無し)。
    static ParsedDec13 ParseDec(string s)
    {
        var res = new ParsedDec13();
        int n = s.Length;
        int i = 0;
        int sign = 1;
        if (i < n)
        {
            int c = (int)s[i];
            if (c == 45)
            {
                sign = -1;
                i = i + 1;
            }
            else if (c == 43)
            {
                i = i + 1;
            }
        }
        int ip = 0;
        bool any = false;
        while (i < n)
        {
            int c = (int)s[i];
            if (c < 48 || c > 57)
                break;
            ip = ip * 10 + (c - 48);
            any = true;
            i = i + 1;
        }
        float v = ip;
        if (i < n && (int)s[i] == 46)
        {
            i = i + 1;
            float f = 0.1f;
            while (i < n)
            {
                int c = (int)s[i];
                if (c < 48 || c > 57)
                    break;
                v = v + (c - 48) * f;
                f = f * 0.1f;
                any = true;
                i = i + 1;
            }
        }
        if (!any)
            return res;
        res.Value = sign * v;
        res.IntPart = sign * ip;
        res.Ok = true;
        return res;
    }

    static float EnvFloat(string name, float fallback)
    {
        var s = Environment.GetEnvironmentVariable(name);
        if (s == null)
            return fallback;
        var p = ParseDec(s);
        return p.Ok ? p.Value : fallback;
    }

    static int EnvInt(string name, int fallback)
    {
        var s = Environment.GetEnvironmentVariable(name);
        if (s == null)
            return fallback;
        var p = ParseDec(s);
        return p.Ok ? p.IntPart : fallback;
    }

    static bool EnvBool(string name, bool fallback)
    {
        var s = Environment.GetEnvironmentVariable(name);
        if (s == null)
            return fallback;
        return s != "0" && s != "false" && s != "FALSE";
    }

    static float Rand01()
    {
        rng = rng * 1664525.0f + 1013904223.0f;
        rng = rng - (float)Math.Floor(rng / 4294967296.0f) * 4294967296.0f;
        return rng / 4294967296.0f;
    }

    static void SpawnOne()
    {
        float life = Rand01() * timeMultiply + 2.0f;
        sprites.Add(new BenchSprite13(Rand01(), Rand01(), life,
            Rand01() * (float)Math.PI * 2.0f, Rand01() * (float)Math.PI * 2.0f,
            Rand01() * 120.0f + 80.0f,
            (int)Math.Floor(Rand01() * spriteRects.Count)));
    }

    // tick % n == 0 相当 (整数剰余は使わない)。v >= 0 前提。
    static bool EveryN(int v, int n)
    {
        return (float)Math.Floor(v / (float)n) * n == v;
    }

    static void UpdateSprites(float fps)
    {
        tick = tick + 1;

        int write = 0;
        for (int i = 0; i < sprites.Count; i++)
        {
            var s = sprites[i];
            s.Update(tickDt);
            if (!s.Dead())
            {
                sprites[write] = s;
                write = write + 1;
            }
        }
        // sprites を write 個に縮める (末尾から)
        while (sprites.Count > write)
            sprites.RemoveAt(sprites.Count - 1);

        var spawn = false;
        if (sprites.Count < maxSprites)
        {
            if (fps > targetFps)
            {
                spawn = true;
                timeMultiply = timeMultiply + 0.7f * tickDt;
            }
            else if (fps > targetFps * 0.5f && EveryN(tick, 2))
            {
                spawn = true;
                timeMultiply = timeMultiply + 0.2f * tickDt;
            }
            else if (fps > targetFps * 0.25f && EveryN(tick, 4))
            {
                spawn = true;
                timeMultiply = timeMultiply - 0.3f * tickDt;
            }
        }

        if (timeMultiply < 1.0f)
            timeMultiply = 1.0f;

        if (spawn)
        {
            int n = burst;
            while (n > 0 && sprites.Count < maxSprites)
            {
                SpawnOne();
                n = n - 1;
            }
        }
    }

    static void SetPx(List<int> px, int x, int y, int r, int g, int b, int a)
    {
        int i = (y * texW + x) * 4;
        px[i] = r;
        px[i + 1] = g;
        px[i + 2] = b;
        px[i + 3] = a;
    }

    static List<int> BuildAtlas(Rect whiteRect)
    {
        var px = new List<int>();
        for (int i = 0; i < texW * texH * 4; i++)
            px.Add(0);
        for (int y = 0; y < cell; y++)
        {
            for (int x = 0; x < cell; x++)
            {
                float nx = (x + 0.5f - cell * 0.5f) / (cell * 0.5f);
                float ny = (y + 0.5f - cell * 0.5f) / (cell * 0.5f);
                float d = (float)Math.Sqrt(nx * nx + ny * ny);
                if (d < 0.92f)
                    SetPx(px, x, y, 255, 255, 255, 255);
                if (Math.Abs(nx) + Math.Abs(ny) < 1.1f)
                    SetPx(px, 16 + x, y, 255, 255, 255, 255);
                if (Math.Max(Math.Abs(nx), Math.Abs(ny)) < 0.78f)
                    SetPx(px, 32 + x, y, 255, 255, 255, 255);
                if (Math.Abs(nx) < 0.24f || Math.Abs(ny) < 0.24f
                    || Math.Abs(nx - ny) < 0.16f || Math.Abs(nx + ny) < 0.16f)
                    SetPx(px, 48 + x, y, 255, 255, 255, 255);
            }
        }
        SetPx(px, whiteRect.X, whiteRect.Y, 255, 255, 255, 255);
        return px;
    }

    static void DrawSprites(SpriteBatch batch, Atlas atlas)
    {
        foreach (var s in sprites)
        {
            float age = (s.TimeInit - s.TimeLeft) / s.TimeInit;
            float pulse = (float)Math.Sin((float)Math.PI * age);
            if (pulse <= 0)
                continue;
            float size = pulse * s.Scale;
            float a = pulse < 0.18f ? pulse / 0.18f : 1.0f;
            float ts = s.TintS;
            float tc = s.TintC;
            float red = 0.58f + 0.42f * ts;
            float green = 0.58f + 0.42f * (-0.5f * ts + sqrt3Half * tc);
            float blue = 0.58f + 0.42f * (-0.5f * ts - sqrt3Half * tc);
            batch.SpriteColor(atlas, spriteRects[s.Kind], s.X * w, s.Y * h,
                size, size, s.Cr, s.Sr, red, green, blue, a);
        }
    }

    static List<int> GlyphRows(string ch)
    {
        switch (ch)
        {
            case "0": return new List<int> { 7, 5, 5, 5, 7 };
            case "1": return new List<int> { 2, 6, 2, 2, 7 };
            case "2": return new List<int> { 7, 1, 7, 4, 7 };
            case "3": return new List<int> { 7, 1, 7, 1, 7 };
            case "4": return new List<int> { 5, 5, 7, 1, 1 };
            case "5": return new List<int> { 7, 4, 7, 1, 7 };
            case "6": return new List<int> { 7, 4, 7, 5, 7 };
            case "7": return new List<int> { 7, 1, 2, 2, 2 };
            case "8": return new List<int> { 7, 5, 7, 5, 7 };
            case "9": return new List<int> { 7, 5, 7, 1, 7 };
            case "A": return new List<int> { 2, 5, 7, 5, 5 };
            case "E": return new List<int> { 7, 4, 6, 4, 7 };
            case "F": return new List<int> { 7, 4, 6, 4, 4 };
            case "G": return new List<int> { 7, 4, 5, 5, 7 };
            case "I": return new List<int> { 7, 2, 2, 2, 7 };
            case "P": return new List<int> { 6, 5, 6, 4, 4 };
            case "R": return new List<int> { 6, 5, 6, 5, 5 };
            case "S": return new List<int> { 7, 4, 7, 1, 7 };
            case "T": return new List<int> { 7, 2, 2, 2, 2 };
            case ":": return new List<int> { 0, 2, 0, 2, 0 };
            default: return new List<int> { 0, 0, 0, 0, 0 };
        }
    }

    static void DrawText(SpriteBatch batch, Atlas atlas, Rect whiteRect,
        int x, int y, string text, int scale, Color color)
    {
        int cursor = x;
        for (int i = 0; i < text.Length; i++)
        {
            var ch = text.Substring(i, 1);
            var rows = GlyphRows(ch);
            for (int row = 0; row < 5; row++)
            {
                int bits = rows[row];
                for (int col = 0; col < 3; col++)
                {
                    if ((bits & (1 << (2 - col))) != 0)
                        batch.Quad(atlas, whiteRect, cursor + col * scale,
                            y + row * scale, scale, scale, color);
                }
            }
            cursor = cursor + 4 * scale;
        }
    }

    static void DrawHud(SpriteBatch batch, Atlas atlas, Rect whiteRect,
        float fps)
    {
        batch.Quad(atlas, whiteRect, 8, 8, 248, 46,
            Color.Rgb(0.0f, 0.0f, 0.0f, 0.56f));
        DrawText(batch, atlas, whiteRect, 16, 16,
            "SPRITES:" + sprites.Count, 3, Color.Rgb(0.90f, 0.96f, 1.0f, 1.0f));
        DrawText(batch, atlas, whiteRect, 16, 36,
            "FPS:" + (int)Math.Floor(fps + 0.5f)
            + " TARGET:" + (int)Math.Floor(targetFps + 0.5f), 2,
            Color.Rgb(0.78f, 1.0f, 0.70f, 1.0f));
    }

    static string FpsText(float fps)
    {
        return "" + (float)Math.Floor(fps * 100.0f + 0.5f) / 100.0f;
    }

    static void MaybePrintScore(float fps)
    {
        if (scoreFrame <= 0 || scorePrinted || tick < scoreFrame)
            return;
        scorePrinted = true;
        Console.WriteLine("SPRITES13_SCORE frame=" + tick + " sprites="
            + sprites.Count + " fps=" + FpsText(fps) + " target="
            + FpsText(targetFps) + " time_multiply=" + FpsText(timeMultiply)
            + " burst=" + burst + " instanced="
            + (useInstancing ? "true" : "false"));
        Lub.Quit();
    }

    public static void OnFrame(float dt)
    {
        var b = batch;
        var a = atlas;
        var m = meter;
        var wr = whiteRect;
        if (b == null || a == null || m == null || wr == null)
            return;

        float fps = m.Tick();
        Profiler.BeginScope("sprites.update");
        if (scoreFrame > 0)
        {
            // The canonical score is intentionally one workload tick per rendered
            // frame. Interactive mode below uses a real-time 60 Hz simulation.
            UpdateSprites(fps);
        }
        else
        {
            var stepNow = step ?? new FixedStep();
            step = stepNow;
            stepNow.Frame(dt, _ => UpdateSprites(fps));
        }
        Profiler.EndScope("sprites.update");

        Profiler.BeginScope("gfx.begin_pass");
        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new float[] { 0.03f, 0.035f, 0.045f, 1.0f },
        });
        Profiler.EndScope("gfx.begin_pass");

        b.Begin();
        Profiler.BeginScope("sprites.draw");
        DrawSprites(b, a);
        Profiler.EndScope("sprites.draw");
        Profiler.BeginScope("sprites.hud");
        DrawHud(b, a, wr, fps);
        Profiler.EndScope("sprites.hud");
        Profiler.BeginScope("batch.flush");
        b.Flush(Gfx.Blend.Alpha);
        Profiler.EndScope("batch.flush");
        Profiler.BeginScope("gfx.end_pass");
        Gfx.EndPass();
        Profiler.EndScope("gfx.end_pass");
        MaybePrintScore(fps);
    }
}
