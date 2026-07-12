// lub の samples/21_iroha (Haxe 版 Iroha21.hx) の TinyC# 版 entry。
// 実行: lub samples/21_iroha/Iroha21.csproj (transpile + watch + hot reload)
// いろはスイカ: 同じ文字の玉がぶつかると「い→ろ→は→に→ほ→へ→と」の
// 順に育つスイカゲーム風サンプル。玉の文字は font_glyph_mesh の
// 三角形化グリフ (MeshText)、HUD は lubx.Text の動的 glyph atlas。
// gameplay rule と物理 desc の数値は Haxe 版に忠実。typedef Ball は class に、
// contact の body key 逆引きは substr + parseInt でなく Dictionary<string, Ball>
// で行う (機能は同一)。
// cs-lib のクラス (Color / Camera2d / SpriteBatch / Rand) は生成 Lua で
// サンプルより後に定義されるため、static 初期化子で参照できない
// (load 時 nil)。それらは onFrame 先頭で遅延生成する。

using System;
using System.Collections.Generic;

/// <summary>玉 1 個 (Haxe 版 typedef Ball と対)。</summary>
public class Ball
{
    public int id;
    public int level;
    public double spawnX; // 宣言用: initial は不変でないと body が作り直される
    public double spawnY;
    public double x;
    public double y;
    public double angle;
    public double age;
    public double overT;
}

public static class Iroha21
{
    const int W = 640;
    const int H = 360;
    const double PPM = 100.0; // physics m -> logical px
    const double HALF_W = 1.15; // 容器の半幅 (m)
    const double WALL_TOP = 3.2; // 壁の上端 (m)
    const double LINE_Y = 2.45; // ゲームオーバー線 (m)
    const double DROP_Y = 2.85; // 投下位置 (m)
    const int LEVELS = 7;

    static string[] CHARS = new string[]
        { "い", "ろ", "は", "に", "ほ", "へ", "と" };
    static double[] RADII = new double[]
        { 0.13, 0.17, 0.22, 0.28, 0.36, 0.46, 0.58 };
    static Color[]? COLORS = null; // onFrame 先頭で遅延生成

    static Camera2d? cam = null;

    static SpriteBatch? batch = null;
    // ゲームオーバー表示用。SpriteBatch は atlas バケツ順で描くので、玉の上に
    // 帯を重ねるには flush を分ける必要がある (バッファも別 prefix にする)。
    static SpriteBatch? overlay = null;
    static Text? hud = null;
    static MeshText? mesh = null;
    static string? ttf = null;
    static int fontVersion = 0;

    static List<Ball> balls = new List<Ball>();
    static int nextId = 0;
    static int nextLevel = 0;
    static int score = 0;
    static int best = 0;
    static double cooldown = 0.0;
    static bool over = false;
    static double t = 0.0;
    static Rand? rng = null;

    // LUB_IROHA_AUTO=1 で自動プレイ (ヘッドレス検証・デモ用)
    static bool auto = os.getenv("LUB_IROHA_AUTO") != null;

    public static void onInit()
    {
        var backend = os.getenv("LUB_BACKEND") ?? "native";
        Lub.config(new ConfigOpts { backend = backend, width = W, height = H });
    }

    public static void onEvent(EventData e)
    {
    }

    public static void onQuit()
    {
    }

    // --- アセット -----------------------------------------------------------

    static bool ensureAssets()
    {
        Io.load_text("samples/21_iroha/data/MPLUS1p-subset.ttf",
            out var text, out var version, out _, out _);
        if (text == null) return false;
        if (ttf == null || fontVersion != version)
        {
            ttf = text;
            fontVersion = version;
            hud = new Text("iroha_hud", text, 20);
            mesh = new MeshText("iroha_mesh", text, fontVersion, W, H);
        }
        return ttf != null && mesh != null;
    }

    // --- ゲーム -------------------------------------------------------------

    static void spawn(double x, double y, int level)
    {
        balls.Add(new Ball
        {
            id = nextId,
            level = level,
            spawnX = x,
            spawnY = y,
            x = x,
            y = y,
            angle = 0.0,
            age = 0.0,
            overT = 0.0,
        });
        nextId++;
    }

    static void reset()
    {
        balls = new List<Ball>();
        score = 0;
        over = false;
        cooldown = 0.3;
    }

    public static void onFrame(double dt)
    {
        if (auto)
            dt = 1.0 / 60.0; // 自動プレイは決定的に進める (headless 検証用)
        t += dt;
        if (dt > 0.1)
            dt = 0.1;
        if (!ensureAssets()) return;
        var hudNow = hud;
        var meshNow = mesh;
        if (hudNow == null || meshNow == null) return;

        var camNow = cam ?? new Camera2d(W, H, PPM, 320.0, 344.0);
        cam = camNow;
        var batchNow = batch ?? new SpriteBatch(W, H);
        batch = batchNow;
        var overlayNow = overlay
            ?? new SpriteBatch(W, H, "lubx_sprite", "iroha_overlay");
        overlay = overlayNow;
        var rngNow = rng ?? new Rand(0x1234567);
        rng = rngNow;
        var colors = COLORS;
        if (colors == null)
        {
            colors = new Color[]
            {
                Color.rgb(0.91, 0.36, 0.36),
                Color.rgb(0.93, 0.60, 0.34),
                Color.rgb(0.93, 0.83, 0.36),
                Color.rgb(0.49, 0.80, 0.42),
                Color.rgb(0.36, 0.72, 0.91),
                Color.rgb(0.50, 0.45, 0.93),
                Color.rgb(0.83, 0.36, 0.91),
            };
            COLORS = colors;
        }

        // --- 入力
        var dropR = RADII[nextLevel];
        var dropX = camNow.mouseWorld().x;
        if (dropX < -(HALF_W - dropR))
            dropX = -(HALF_W - dropR);
        if (dropX > HALF_W - dropR)
            dropX = HALF_W - dropR;
        cooldown -= dt;
        var click = Input.mouse_pressed() || Input.key_pressed("space");
        if (auto && cooldown <= 0.0 && !over)
        {
            click = true;
            dropX = (rngNow.nextFloat() * 2.0 - 1.0) * (HALF_W - dropR);
        }
        if (over)
        {
            if (click)
                reset();
        }
        else if (click && cooldown <= 0.0)
        {
            spawn(dropX, DROP_Y, nextLevel);
            var pickTable = new int[] { 0, 0, 0, 1, 1, 2 };
            nextLevel = pickTable[(int)Math.Floor(rngNow.nextFloat() * 6.0)];
            cooldown = 0.45;
            Audio.audio_play(Sfx.blip(420, 260, 0.06, 0.3));
        }

        // --- 物理 (immediate mode: 生きている玉だけ毎フレーム宣言する)
        var world = Phys2d.phys2d_world("iroha", new WorldOpts
        {
            gravity = new Vec2d { x = 0.0, y = -10.0 },
            fixedDt = 1.0 / 120.0,
            substeps = 4,
            maxSteps = 4,
        });
        if (world == null) return;
        Phys2d.phys2d_begin(world);

        var arena = Phys2d.phys2d_body(world, "arena", new BodyDesc
        {
            type = Phys2d.STATIC,
            initial = new InitialState { x = 0.0, y = 0.0 },
        });
        if (arena == null) return;
        Phys2d.phys2d_box(arena, "floor", new BoxDesc
        {
            hx = HALF_W + 0.3,
            hy = 0.1,
            cy = -0.1,
            friction = 0.5,
        });
        Phys2d.phys2d_box(arena, "wall_l", new BoxDesc
        {
            hx = 0.1,
            hy = WALL_TOP * 0.5,
            cx = -(HALF_W + 0.1),
            cy = WALL_TOP * 0.5,
            friction = 0.3,
        });
        Phys2d.phys2d_box(arena, "wall_r", new BoxDesc
        {
            hx = 0.1,
            hy = WALL_TOP * 0.5,
            cx = HALF_W + 0.1,
            cy = WALL_TOP * 0.5,
            friction = 0.3,
        });

        var refs = new Dictionary<int, BodyRef>();
        foreach (var ball in balls)
        {
            var b = Phys2d.phys2d_body(world, "ball:" + ball.id, new BodyDesc
            {
                type = Phys2d.DYNAMIC,
                initial = new InitialState { x = ball.spawnX, y = ball.spawnY },
            });
            if (b == null) return;
            Phys2d.phys2d_circle(b, "c", new CircleDesc
            {
                r = RADII[ball.level],
                density = 1.0,
                friction = 0.35,
                restitution = 0.12,
                contact = true,
            });
            refs[ball.id] = b;
        }

        Phys2d.phys2d_step(world, dt);

        // contact の body key ("ball:<id>") からの逆引き用
        var byKey = new Dictionary<string, Ball>();
        foreach (var ball in balls)
        {
            byKey["ball:" + ball.id] = ball;
            if (!refs.TryGetValue(ball.id, out var bodyRef)) continue;
            var p = Phys2d.phys2d_pose(bodyRef);
            if (p == null) continue;
            ball.x = p.x;
            ball.y = p.y;
            ball.angle = p.angle;
            ball.age += dt;
        }

        // --- 合体: 同じ文字同士の contact begin で次の文字へ
        if (!over)
        {
            var contacts = Phys2d.phys2d_contacts(world, "begin");
            var merged = new Dictionary<int, bool>();
            bool anyMerged = false;
            foreach (var c in contacts)
            {
                if (!byKey.TryGetValue(c.a.body, out var b1)) continue;
                if (!byKey.TryGetValue(c.b.body, out var b2)) continue;
                if (b1.level != b2.level) continue;
                if (merged.ContainsKey(b1.id) || merged.ContainsKey(b2.id))
                    continue;
                merged[b1.id] = true;
                merged[b2.id] = true;
                anyMerged = true;
                var level = b1.level;
                if (level < LEVELS - 1)
                    spawn((b1.x + b2.x) * 0.5, (b1.y + b2.y) * 0.5, level + 1);
                score += (level + 1) * (level + 1);
                if (score > best)
                    best = score;
                Audio.audio_play(Sfx.blip(400, 840, 0.12, 0.35),
                    new PlayOpts { pitch = 1.0 + level * 0.15 });
            }
            if (anyMerged)
            {
                var kept = new List<Ball>();
                foreach (var ball in balls)
                {
                    if (!merged.ContainsKey(ball.id))
                        kept.Add(ball);
                }
                balls = kept;
            }
        }

        // --- ゲームオーバー判定: 落下直後を除き、線より上に居座ったら終わり
        if (!over)
        {
            foreach (var ball in balls)
            {
                var top = ball.y + RADII[ball.level];
                if (top > LINE_Y && ball.age > 1.0)
                    ball.overT += dt;
                else
                    ball.overT = 0.0;
                if (ball.overT > 1.0)
                {
                    over = true;
                    Audio.audio_play(Sfx.noise(0.4, 0.5, 0x2468ace));
                }
            }
        }

        // --- 描画
        Gfx.begin_pass(new PassOpts
        {
            target = Gfx.main_tex,
            clear_color = new double[] { 0.10, 0.09, 0.13, 1.0 },
        });
        batchNow.begin();

        // 容器
        var wallCol = Color.rgb(0.35, 0.32, 0.42);
        batchNow.rect(camNow.sx(-HALF_W) - 8, camNow.sy(WALL_TOP), 8, WALL_TOP * PPM,
            wallCol);
        batchNow.rect(camNow.sx(HALF_W), camNow.sy(WALL_TOP), 8, WALL_TOP * PPM,
            wallCol);
        batchNow.rect(camNow.sx(-HALF_W) - 8, camNow.originY, HALF_W * 2 * PPM + 16, 8,
            wallCol);

        // ゲームオーバー線
        var lineBlink = over ? 1.0 : 0.25 + 0.15 * Math.Sin(t * 4.0);
        batchNow.rect(camNow.sx(-HALF_W), camNow.sy(LINE_Y), HALF_W * 2 * PPM, 2,
            Color.rgb(0.9, 0.3, 0.3, lineBlink));

        // 玉 (sprite は本体、上に mesh グリフ)
        foreach (var ball in balls)
        {
            var r = RADII[ball.level] * PPM;
            var c = colors[ball.level];
            batchNow.disc(camNow.sx(ball.x), camNow.sy(ball.y), r, c);
        }

        // 投下プレビュー
        if (!over)
        {
            var r = dropR * PPM;
            var c = colors[nextLevel];
            batchNow.disc(camNow.sx(dropX), camNow.sy(DROP_Y), r,
                Color.rgb(c.r, c.g, c.b, 0.5 + 0.2 * Math.Sin(t * 6.0)));
        }

        // HUD (bitmap 小サイズレジーム)
        hudNow.draw(batchNow, "スコア " + score, 12, 26);
        hudNow.draw(batchNow, "ベスト " + best, 12, 50,
            Color.rgb(0.8, 0.8, 0.8, 0.8));
        hudNow.draw(batchNow, "いろはにほへと", 500, 26,
            Color.rgb(0.7, 0.7, 0.8, 0.9), 0.8);

        batchNow.flush();

        // mesh グリフ (拡大レジーム): 玉の文字は物理の回転ごと描く
        var ink = Color.rgb(0.12, 0.10, 0.14, 0.9);
        foreach (var ball in balls)
        {
            meshNow.Char(CHARS[ball.level], camNow.sx(ball.x), camNow.sy(ball.y),
                RADII[ball.level] * PPM * 1.3, ball.angle, ink, true);
        }
        if (!over)
            meshNow.Char(CHARS[nextLevel], camNow.sx(dropX), camNow.sy(DROP_Y),
                dropR * PPM * 1.3, 0.0,
                Color.rgb(ink.r, ink.g, ink.b, 0.6), true);

        if (over)
        {
            // 帯とメッセージは玉の上に重ねたいので別 batch で flush を分ける
            overlayNow.begin();
            overlayNow.rect(0, 108, W, 132, Color.rgb(0.05, 0.04, 0.07, 0.85));
            var msg = "クリックでもういちど";
            hudNow.draw(overlayNow, msg,
                camNow.originX - hudNow.width(msg) * 0.5, 222);
            overlayNow.flush();
            meshNow.textCentered("おしまい", camNow.originX, 190, 64,
                Color.rgb(0.95, 0.92, 0.85));
        }

        Gfx.end_pass();
    }
}
