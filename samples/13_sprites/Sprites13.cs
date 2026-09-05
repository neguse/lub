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
    public double Value = 0.0;
    public int IntPart = 0;
}

public class BenchSprite13
{
    public double X;
    public double Y;
    public double TimeInit;
    public double TimeLeft;
    public double R;
    public double Dr;
    public double Cr;
    public double Sr;
    public double StepCr;
    public double StepSr;
    public double TintS;
    public double TintC;
    public double TintStepS;
    public double TintStepC;
    public double Scale;
    public int Kind;

    public BenchSprite13(double x, double y, double timeInit, double r,
        double dr, double scale, int kind)
    {
        this.X = x;
        this.Y = y;
        this.TimeInit = timeInit;
        this.TimeLeft = timeInit;
        this.R = r;
        this.Dr = dr;
        this.Cr = Math.Cos(r);
        this.Sr = Math.Sin(r);
        double step = dr / 60.0;
        this.StepCr = Math.Cos(step);
        this.StepSr = Math.Sin(step);
        double phase = r * 3.0;
        this.TintS = Math.Sin(phase);
        this.TintC = Math.Cos(phase);
        double tintStep = step * 3.0;
        this.TintStepS = Math.Sin(tintStep);
        this.TintStepC = Math.Cos(tintStep);
        this.Scale = scale;
        this.Kind = kind;
    }

    public void Update(double dt)
    {
        R = R + dt * Dr;
        TimeLeft = TimeLeft - dt;
        double nextCr = Cr * StepCr - Sr * StepSr;
        Sr = Cr * StepSr + Sr * StepCr;
        Cr = nextCr;
        double nextTintS = TintS * TintStepC + TintC * TintStepS;
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
    const double dt = 1.0 / 60.0;
    const int texW = 80;
    const int texH = 16;
    const int cell = 16;
    const double sqrt3Half = 0.8660254037844386;

    static List<BenchSprite13> sprites = new List<BenchSprite13>();
    static SpriteBatch? batch = null;
    static Atlas? atlas = null;
    static FpsMeter? meter = null;
    static int tick = 0;
    static double rng = 305419896.0;
    static double timeMultiply = 4.0;
    static double targetFps = 60.0;
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
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "native";
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

        targetFps = EnvFloat("LUB_SPRITE_TARGET_FPS", 60.0);
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
        double v = ip;
        if (i < n && (int)s[i] == 46)
        {
            i = i + 1;
            double f = 0.1;
            while (i < n)
            {
                int c = (int)s[i];
                if (c < 48 || c > 57)
                    break;
                v = v + (c - 48) * f;
                f = f * 0.1;
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

    static double EnvFloat(string name, double fallback)
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

    static double Rand01()
    {
        rng = rng * 1664525.0 + 1013904223.0;
        rng = rng - Math.Floor(rng / 4294967296.0) * 4294967296.0;
        return rng / 4294967296.0;
    }

    static void SpawnOne()
    {
        double life = Rand01() * timeMultiply + 2.0;
        sprites.Add(new BenchSprite13(Rand01(), Rand01(), life,
            Rand01() * Math.PI * 2.0, Rand01() * Math.PI * 2.0,
            Rand01() * 120.0 + 80.0,
            (int)Math.Floor(Rand01() * spriteRects.Count)));
    }

    // tick % n == 0 相当 (整数剰余は使わない)。v >= 0 前提。
    static bool EveryN(int v, int n)
    {
        return Math.Floor(v / (double)n) * n == v;
    }

    static void UpdateSprites(double fps)
    {
        tick = tick + 1;

        int write = 0;
        for (int i = 0; i < sprites.Count; i++)
        {
            var s = sprites[i];
            s.Update(dt);
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
                timeMultiply = timeMultiply + 0.7 * dt;
            }
            else if (fps > targetFps * 0.5 && EveryN(tick, 2))
            {
                spawn = true;
                timeMultiply = timeMultiply + 0.2 * dt;
            }
            else if (fps > targetFps * 0.25 && EveryN(tick, 4))
            {
                spawn = true;
                timeMultiply = timeMultiply - 0.3 * dt;
            }
        }

        if (timeMultiply < 1.0)
            timeMultiply = 1.0;

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
                double nx = (x + 0.5 - cell * 0.5) / (cell * 0.5);
                double ny = (y + 0.5 - cell * 0.5) / (cell * 0.5);
                double d = Math.Sqrt(nx * nx + ny * ny);
                if (d < 0.92)
                    SetPx(px, x, y, 255, 255, 255, 255);
                if (Math.Abs(nx) + Math.Abs(ny) < 1.1)
                    SetPx(px, 16 + x, y, 255, 255, 255, 255);
                if (Math.Max(Math.Abs(nx), Math.Abs(ny)) < 0.78)
                    SetPx(px, 32 + x, y, 255, 255, 255, 255);
                if (Math.Abs(nx) < 0.24 || Math.Abs(ny) < 0.24
                    || Math.Abs(nx - ny) < 0.16 || Math.Abs(nx + ny) < 0.16)
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
            double age = (s.TimeInit - s.TimeLeft) / s.TimeInit;
            double pulse = Math.Sin(Math.PI * age);
            if (pulse <= 0)
                continue;
            double size = pulse * s.Scale;
            double a = pulse < 0.18 ? pulse / 0.18 : 1.0;
            double ts = s.TintS;
            double tc = s.TintC;
            double red = 0.58 + 0.42 * ts;
            double green = 0.58 + 0.42 * (-0.5 * ts + sqrt3Half * tc);
            double blue = 0.58 + 0.42 * (-0.5 * ts - sqrt3Half * tc);
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
        double fps)
    {
        batch.Quad(atlas, whiteRect, 8, 8, 248, 46,
            Color.Rgb(0.0, 0.0, 0.0, 0.56));
        DrawText(batch, atlas, whiteRect, 16, 16,
            "SPRITES:" + sprites.Count, 3, Color.Rgb(0.90, 0.96, 1.0, 1.0));
        DrawText(batch, atlas, whiteRect, 16, 36,
            "FPS:" + (int)Math.Floor(fps + 0.5)
            + " TARGET:" + (int)Math.Floor(targetFps + 0.5), 2,
            Color.Rgb(0.78, 1.0, 0.70, 1.0));
    }

    static string FpsText(double fps)
    {
        return "" + Math.Floor(fps * 100.0 + 0.5) / 100.0;
    }

    static void MaybePrintScore(double fps)
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

    public static void OnFrame(double dt)
    {
        var b = batch;
        var a = atlas;
        var m = meter;
        var wr = whiteRect;
        if (b == null || a == null || m == null || wr == null)
            return;

        double fps = m.Tick();
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
            ClearColor = new double[] { 0.03, 0.035, 0.045, 1.0 },
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
