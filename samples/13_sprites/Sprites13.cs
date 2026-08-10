// lub の samples/13_sprites (Haxe 版 Sprites13.hx) の TinyC# 版 entry。
// 実行: lub samples/13_sprites/Sprites13.csproj (transpile + watch + hot reload)
// スコアリング (LCG rand01 / spawn 閾値 / SPRITES13_SCORE の書式) は Haxe 版と
// 同一。tcs 制約による置換: Std.parseFloat/parseInt → parseDec (@byte 走査の
// 10 進 leading parse、out は extern stub 専用なので結果は class で返す)、
// tick % n == 0 → everyN (floor)、Std.int → Math.Floor (対象値は常に非負)。
// spriteRects/whiteRect は static 初期化子で作れない (Lua 出力は sample →
// cs-lib の順なので Rect が未定義) ため onInit で作る。

using System;
using System.Collections.Generic;
using static @string;

/// <summary>parseDec の結果 (tcs では自作関数の out が使えないため class 返し)。</summary>
public class ParsedDec13
{
    public bool ok = false;
    public float value = 0.0f;
    public int intPart = 0;
}

public class BenchSprite13
{
    public float x;
    public float y;
    public float timeInit;
    public float timeLeft;
    public float r;
    public float dr;
    public float cr;
    public float sr;
    public float stepCr;
    public float stepSr;
    public float tintS;
    public float tintC;
    public float tintStepS;
    public float tintStepC;
    public float scale;
    public int kind;

    public BenchSprite13(float x, float y, float timeInit, float r,
        float dr, float scale, int kind)
    {
        this.x = x;
        this.y = y;
        this.timeInit = timeInit;
        this.timeLeft = timeInit;
        this.r = r;
        this.dr = dr;
        this.cr = (float)Math.Cos(r);
        this.sr = (float)Math.Sin(r);
        float step = dr / 60.0f;
        this.stepCr = (float)Math.Cos(step);
        this.stepSr = (float)Math.Sin(step);
        float phase = r * 3.0f;
        this.tintS = (float)Math.Sin(phase);
        this.tintC = (float)Math.Cos(phase);
        float tintStep = step * 3.0f;
        this.tintStepS = (float)Math.Sin(tintStep);
        this.tintStepC = (float)Math.Cos(tintStep);
        this.scale = scale;
        this.kind = kind;
    }

    public void update(float dt)
    {
        r = r + dt * dr;
        timeLeft = timeLeft - dt;
        float nextCr = cr * stepCr - sr * stepSr;
        sr = cr * stepSr + sr * stepCr;
        cr = nextCr;
        float nextTintS = tintS * tintStepC + tintC * tintStepS;
        tintC = tintC * tintStepC - tintS * tintStepS;
        tintS = nextTintS;
    }

    public bool dead()
    {
        return timeLeft < 0;
    }
}

public static class Sprites13
{
    const int W = 640;
    const int H = 480;
    const float DT = 1.0f / 60.0f;
    const int TEX_W = 80;
    const int TEX_H = 16;
    const int CELL = 16;
    const float SQRT3_HALF = 0.8660254037844386f;

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

    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND");
        Lub.config(new ConfigOpts { backend = backend, width = W, height = H });

        spriteRects = new List<Rect>
        {
            new Rect(0, 0, CELL, CELL),
            new Rect(16, 0, CELL, CELL),
            new Rect(32, 0, CELL, CELL),
            new Rect(48, 0, CELL, CELL),
        };
        var wr = new Rect(64, 0, 1, 1);
        whiteRect = wr;

        targetFps = envFloat("LUB_SPRITE_TARGET_FPS", 60.0f);
        maxSprites = envInt("LUB_SPRITE_MAX", 200000);
        burst = envInt("LUB_SPRITE_BURST", 1);
        scoreFrame = envInt("LUB_SPRITE_SCORE_FRAME", 0);
        useInstancing = envBool("LUB_SPRITE_INSTANCED", true);
        if (burst < 1)
            burst = 1;

        batch = new SpriteBatch(W, H, "sprites13_shader", "sprites13_batch",
            useInstancing);
        atlas = Atlas.fromPixels("sprites13_atlas", TEX_W, TEX_H,
            buildAtlas(wr), 1,
            new TextureOpts { filter = Gfx.LINEAR, wrap = Gfx.CLAMP });
        meter = new FpsMeter(targetFps);
    }

    // Std.parseFloat/parseInt 相当 (tcs に数値 parse API が無いので手書き)。
    // 先頭の [+-]?digits[.digits] を読み、後続の文字は無視する。ok=false は
    // 数字ゼロ個 (Haxe 版の NaN / null 判定と対)。10 進のみ (hex/指数は無し)。
    static ParsedDec13 parseDec(string s)
    {
        var res = new ParsedDec13();
        int n = len(s);
        int i = 1;
        int sign = 1;
        if (i <= n)
        {
            int c = @byte(s, i);
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
        while (i <= n)
        {
            int c = @byte(s, i);
            if (c < 48 || c > 57)
                break;
            ip = ip * 10 + (c - 48);
            any = true;
            i = i + 1;
        }
        float v = ip;
        if (i <= n && @byte(s, i) == 46)
        {
            i = i + 1;
            float f = 0.1f;
            while (i <= n)
            {
                int c = @byte(s, i);
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
        res.value = sign * v;
        res.intPart = sign * ip;
        res.ok = true;
        return res;
    }

    static float envFloat(string name, float fallback)
    {
        var s = os.getenv(name);
        if (s == null)
            return fallback;
        var p = parseDec(s);
        return p.ok ? p.value : fallback;
    }

    static int envInt(string name, int fallback)
    {
        var s = os.getenv(name);
        if (s == null)
            return fallback;
        var p = parseDec(s);
        return p.ok ? p.intPart : fallback;
    }

    static bool envBool(string name, bool fallback)
    {
        var s = os.getenv(name);
        if (s == null)
            return fallback;
        return s != "0" && s != "false" && s != "FALSE";
    }

    static float rand01()
    {
        rng = rng * 1664525.0f + 1013904223.0f;
        rng = rng - (float)Math.Floor(rng / 4294967296.0f) * 4294967296.0f;
        return rng / 4294967296.0f;
    }

    static void spawnOne()
    {
        float life = rand01() * timeMultiply + 2.0f;
        sprites.Add(new BenchSprite13(rand01(), rand01(), life,
            rand01() * (float)Math.PI * 2.0f, rand01() * (float)Math.PI * 2.0f,
            rand01() * 120.0f + 80.0f,
            (int)Math.Floor(rand01() * spriteRects.Count)));
    }

    // Haxe 版の tick % n == 0 相当 (整数剰余は使わない)。v >= 0 前提。
    static bool everyN(int v, int n)
    {
        return (float)Math.Floor(v / (float)n) * n == v;
    }

    static void updateSprites(float fps)
    {
        tick = tick + 1;

        int write = 0;
        for (int i = 0; i < sprites.Count; i++)
        {
            var s = sprites[i];
            s.update(DT);
            if (!s.dead())
            {
                sprites[write] = s;
                write = write + 1;
            }
        }
        // Haxe 版 sprites.resize(write) 相当 (末尾から縮める)
        while (sprites.Count > write)
            sprites.RemoveAt(sprites.Count - 1);

        var spawn = false;
        if (sprites.Count < maxSprites)
        {
            if (fps > targetFps)
            {
                spawn = true;
                timeMultiply = timeMultiply + 0.7f * DT;
            }
            else if (fps > targetFps * 0.5f && everyN(tick, 2))
            {
                spawn = true;
                timeMultiply = timeMultiply + 0.2f * DT;
            }
            else if (fps > targetFps * 0.25f && everyN(tick, 4))
            {
                spawn = true;
                timeMultiply = timeMultiply - 0.3f * DT;
            }
        }

        if (timeMultiply < 1.0f)
            timeMultiply = 1.0f;

        if (spawn)
        {
            int n = burst;
            while (n > 0 && sprites.Count < maxSprites)
            {
                spawnOne();
                n = n - 1;
            }
        }
    }

    static void setPx(List<int> px, int x, int y, int r, int g, int b, int a)
    {
        int i = (y * TEX_W + x) * 4;
        px[i] = r;
        px[i + 1] = g;
        px[i + 2] = b;
        px[i + 3] = a;
    }

    static List<int> buildAtlas(Rect whiteRect)
    {
        var px = new List<int>();
        for (int i = 0; i < TEX_W * TEX_H * 4; i++)
            px.Add(0);
        for (int y = 0; y < CELL; y++)
        {
            for (int x = 0; x < CELL; x++)
            {
                float nx = (x + 0.5f - CELL * 0.5f) / (CELL * 0.5f);
                float ny = (y + 0.5f - CELL * 0.5f) / (CELL * 0.5f);
                float d = (float)Math.Sqrt(nx * nx + ny * ny);
                if (d < 0.92f)
                    setPx(px, x, y, 255, 255, 255, 255);
                if (Math.Abs(nx) + Math.Abs(ny) < 1.1f)
                    setPx(px, 16 + x, y, 255, 255, 255, 255);
                if (Math.Max(Math.Abs(nx), Math.Abs(ny)) < 0.78f)
                    setPx(px, 32 + x, y, 255, 255, 255, 255);
                if (Math.Abs(nx) < 0.24f || Math.Abs(ny) < 0.24f
                    || Math.Abs(nx - ny) < 0.16f || Math.Abs(nx + ny) < 0.16f)
                    setPx(px, 48 + x, y, 255, 255, 255, 255);
            }
        }
        setPx(px, whiteRect.x, whiteRect.y, 255, 255, 255, 255);
        return px;
    }

    static void drawSprites(SpriteBatch batch, Atlas atlas)
    {
        foreach (var s in sprites)
        {
            float age = (s.timeInit - s.timeLeft) / s.timeInit;
            float pulse = (float)Math.Sin((float)Math.PI * age);
            if (pulse <= 0)
                continue;
            float size = pulse * s.scale;
            float a = pulse < 0.18f ? pulse / 0.18f : 1.0f;
            float ts = s.tintS;
            float tc = s.tintC;
            float red = 0.58f + 0.42f * ts;
            float green = 0.58f + 0.42f * (-0.5f * ts + SQRT3_HALF * tc);
            float blue = 0.58f + 0.42f * (-0.5f * ts - SQRT3_HALF * tc);
            batch.spriteColor(atlas, spriteRects[s.kind], s.x * W, s.y * H,
                size, size, s.cr, s.sr, red, green, blue, a);
        }
    }

    static List<int> glyphRows(string ch)
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

    static void drawText(SpriteBatch batch, Atlas atlas, Rect whiteRect,
        int x, int y, string text, int scale, Color color)
    {
        int cursor = x;
        for (int i = 0; i < text.Length; i++)
        {
            var ch = text.Substring(i, 1);
            var rows = glyphRows(ch);
            for (int row = 0; row < 5; row++)
            {
                int bits = rows[row];
                for (int col = 0; col < 3; col++)
                {
                    if ((bits & (1 << (2 - col))) != 0)
                        batch.quad(atlas, whiteRect, cursor + col * scale,
                            y + row * scale, scale, scale, color);
                }
            }
            cursor = cursor + 4 * scale;
        }
    }

    static void drawHud(SpriteBatch batch, Atlas atlas, Rect whiteRect,
        float fps)
    {
        batch.quad(atlas, whiteRect, 8, 8, 248, 46,
            Color.rgb(0.0f, 0.0f, 0.0f, 0.56f));
        drawText(batch, atlas, whiteRect, 16, 16,
            "SPRITES:" + sprites.Count, 3, Color.rgb(0.90f, 0.96f, 1.0f, 1.0f));
        drawText(batch, atlas, whiteRect, 16, 36,
            "FPS:" + (int)Math.Floor(fps + 0.5f)
            + " TARGET:" + (int)Math.Floor(targetFps + 0.5f), 2,
            Color.rgb(0.78f, 1.0f, 0.70f, 1.0f));
    }

    static string fpsText(float fps)
    {
        return "" + (float)Math.Floor(fps * 100.0f + 0.5f) / 100.0f;
    }

    static void maybePrintScore(float fps)
    {
        if (scoreFrame <= 0 || scorePrinted || tick < scoreFrame)
            return;
        scorePrinted = true;
        Console.WriteLine("SPRITES13_SCORE frame=" + tick + " sprites="
            + sprites.Count + " fps=" + fpsText(fps) + " target="
            + fpsText(targetFps) + " time_multiply=" + fpsText(timeMultiply)
            + " burst=" + burst + " instanced="
            + (useInstancing ? "true" : "false"));
        Lub.quit();
    }

    public static void onFrame(float dt)
    {
        var b = batch;
        var a = atlas;
        var m = meter;
        var wr = whiteRect;
        if (b == null || a == null || m == null || wr == null)
            return;

        float fps = m.tick();
        Profiler.begin_scope("sprites.update");
        if (scoreFrame > 0)
        {
            // The canonical score is intentionally one workload tick per rendered
            // frame. Interactive mode below uses a real-time 60 Hz simulation.
            updateSprites(fps);
        }
        else
        {
            var stepNow = step ?? new FixedStep();
            step = stepNow;
            stepNow.frame(dt, _ => updateSprites(fps));
        }
        Profiler.end_scope("sprites.update");

        Profiler.begin_scope("gfx.begin_pass");
        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new float[] { 0.03f, 0.035f, 0.045f, 1.0f },
        });
        Profiler.end_scope("gfx.begin_pass");

        b.begin();
        Profiler.begin_scope("sprites.draw");
        drawSprites(b, a);
        Profiler.end_scope("sprites.draw");
        Profiler.begin_scope("sprites.hud");
        drawHud(b, a, wr, fps);
        Profiler.end_scope("sprites.hud");
        Profiler.begin_scope("batch.flush");
        b.flush(Gfx.ALPHA);
        Profiler.end_scope("batch.flush");
        Profiler.begin_scope("gfx.end_pass");
        Gfx.end_pass();
        Profiler.end_scope("gfx.end_pass");
        maybePrintScore(fps);
    }
}
