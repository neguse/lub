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
using static Lub;

/// <summary>玉 1 個 (Haxe 版 typedef Ball と対)。</summary>
public class Ball
{
    public int Id;
    public int Level;
    public double SpawnX; // 宣言用: initial は不変でないと body が作り直される
    public double SpawnY;
    public double X;
    public double Y;
    public double Angle;
    public double Age;
    public double OverT;
}

public static class Iroha21
{
    const int w = 640;
    const int h = 360;
    const double ppm = 100.0; // physics m -> logical px
    const double halfW = 1.15; // 容器の半幅 (m)
    const double wallTop = 3.2; // 壁の上端 (m)
    const double lineY = 2.45; // ゲームオーバー線 (m)
    const double dropY = 2.85; // 投下位置 (m)
    const int levels = 7;

    static string[] chars = new string[]
        { "い", "ろ", "は", "に", "ほ", "へ", "と" };
    static double[] radii = new double[]
        { 0.13, 0.17, 0.22, 0.28, 0.36, 0.46, 0.58 };
    static Color[]? _colors = null; // onFrame 先頭で遅延生成

    static Camera2d? cam = null;

    static SpriteBatch? batch = null;
    // ゲームオーバー表示用。SpriteBatch は atlas バケツ順で描くので、玉の上に
    // 帯を重ねるには flush を分ける必要がある (バッファも別 prefix にする)。
    static SpriteBatch? overlay = null;
    static Text? hud = null;
    static MeshText? mesh = null;
    const string FontPath = "samples/21_iroha/data/MPLUS1p-subset.ttf";
    static bool fontLoaded = false;
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
    static bool auto = Environment.GetEnvironmentVariable("LUB_IROHA_AUTO") != null;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "native";
        Lub.Config(new ConfigOpts { Backend = backend, Width = w, Height = h });
    }

    public static void OnEvent(EventData e)
    {
    }

    public static void OnQuit()
    {
    }

    // --- アセット -----------------------------------------------------------

    static bool EnsureAssets()
    {
        Io.LoadBytes(FontPath, out var bytes, out var version, out _, out _);
        if (bytes == null) return false;
        if (!fontLoaded || fontVersion != version)
        {
            fontLoaded = true;
            fontVersion = version;
            hud = new Text("iroha_hud", FontPath, 20);
            mesh = new MeshText("iroha_mesh", FontPath, fontVersion, w, h);
        }
        return fontLoaded && mesh != null;
    }

    // --- ゲーム -------------------------------------------------------------

    static void Spawn(double x, double y, int level)
    {
        balls.Add(new Ball
        {
            Id = nextId,
            Level = level,
            SpawnX = x,
            SpawnY = y,
            X = x,
            Y = y,
            Angle = 0.0,
            Age = 0.0,
            OverT = 0.0,
        });
        nextId++;
    }

    static void Reset()
    {
        balls = new List<Ball>();
        score = 0;
        over = false;
        cooldown = 0.3;
    }

    public static void OnFrame(double dt)
    {
        t += dt;
        if (dt > 0.1)
            dt = 0.1;
        if (!EnsureAssets()) return;
        var hudNow = hud;
        var meshNow = mesh;
        if (hudNow == null || meshNow == null) return;

        var camNow = cam ?? new Camera2d(w, h, ppm, 320.0, 344.0);
        cam = camNow;
        var batchNow = batch ?? new SpriteBatch(w, h);
        batch = batchNow;
        var overlayNow = overlay
            ?? new SpriteBatch(w, h, "lubx_sprite", "iroha_overlay");
        overlay = overlayNow;
        var rngNow = rng ?? new Rand(0x1234567);
        rng = rngNow;
        var colors = _colors;
        if (colors == null)
        {
            colors = new Color[]
            {
                Color.Rgb(0.91, 0.36, 0.36),
                Color.Rgb(0.93, 0.60, 0.34),
                Color.Rgb(0.93, 0.83, 0.36),
                Color.Rgb(0.49, 0.80, 0.42),
                Color.Rgb(0.36, 0.72, 0.91),
                Color.Rgb(0.50, 0.45, 0.93),
                Color.Rgb(0.83, 0.36, 0.91),
            };
            _colors = colors;
        }

        // --- 入力
        var dropR = radii[nextLevel];
        var dropX = camNow.MouseWorld().X;
        if (dropX < -(halfW - dropR))
            dropX = -(halfW - dropR);
        if (dropX > halfW - dropR)
            dropX = halfW - dropR;
        cooldown -= dt;
        var click = Input.MousePressed() || Input.KeyPressed("space");
        if (auto && cooldown <= 0.0 && !over)
        {
            click = true;
            dropX = (rngNow.NextFloat() * 2.0 - 1.0) * (halfW - dropR);
        }
        if (over)
        {
            if (click)
                Reset();
        }
        else if (click && cooldown <= 0.0)
        {
            Spawn(dropX, dropY, nextLevel);
            var pickTable = new int[] { 0, 0, 0, 1, 1, 2 };
            nextLevel = pickTable[(int)Math.Floor(rngNow.NextFloat() * 6.0)];
            cooldown = 0.45;
            Audio.Play(Sfx.Blip(420, 260, 0.06, 0.3));
        }

        // --- 物理 (immediate mode: 生きている玉だけ毎フレーム宣言する)
        var world = Phys2d.World("iroha", new WorldOpts
        {
            Gravity = new Vec2d { X = 0.0, Y = -10.0 },
            FixedDt = 1.0 / 120.0,
            Substeps = 4,
            MaxSteps = 4,
        });
        if (world == null) return;
        Phys2d.Begin(world);

        var arena = Phys2d.Body(world, "arena", new BodyDesc
        {
            Type = Phys2d.BodyType.Static,
            Initial = new InitialState { X = 0.0, Y = 0.0 },
        });
        if (arena == null) return;
        Phys2d.Box(arena, "floor", new BoxDesc
        {
            Hx = halfW + 0.3,
            Hy = 0.1,
            Cy = -0.1,
            Friction = 0.5,
        });
        Phys2d.Box(arena, "wall_l", new BoxDesc
        {
            Hx = 0.1,
            Hy = wallTop * 0.5,
            Cx = -(halfW + 0.1),
            Cy = wallTop * 0.5,
            Friction = 0.3,
        });
        Phys2d.Box(arena, "wall_r", new BoxDesc
        {
            Hx = 0.1,
            Hy = wallTop * 0.5,
            Cx = halfW + 0.1,
            Cy = wallTop * 0.5,
            Friction = 0.3,
        });

        var refs = new Dictionary<int, BodyRef>();
        foreach (var ball in balls)
        {
            var b = Phys2d.Body(world, "ball:" + ball.Id, new BodyDesc
            {
                Type = Phys2d.BodyType.Dynamic,
                Initial = new InitialState { X = ball.SpawnX, Y = ball.SpawnY },
            });
            if (b == null) return;
            Phys2d.Circle(b, "c", new CircleDesc
            {
                R = radii[ball.Level],
                Density = 1.0,
                Friction = 0.35,
                Restitution = 0.12,
                Contact = true,
            });
            refs[ball.Id] = b;
        }

        Phys2d.Step(world, dt);

        // contact の body key ("ball:<id>") からの逆引き用
        var byKey = new Dictionary<string, Ball>();
        foreach (var ball in balls)
        {
            byKey["ball:" + ball.Id] = ball;
            if (!refs.TryGetValue(ball.Id, out var bodyRef)) continue;
            var p = Phys2d.Pose(bodyRef);
            if (p == null) continue;
            ball.X = p.X;
            ball.Y = p.Y;
            ball.Angle = p.Angle;
            ball.Age += dt;
        }

        // --- 合体: 同じ文字同士の contact begin で次の文字へ
        if (!over)
        {
            var contacts = Phys2d.Contacts(world, Phys2d.EventKind.Begin);
            var merged = new Dictionary<int, bool>();
            bool anyMerged = false;
            foreach (var c in contacts)
            {
                if (!byKey.TryGetValue(c.A.Body, out var b1)) continue;
                if (!byKey.TryGetValue(c.B.Body, out var b2)) continue;
                if (b1.Level != b2.Level) continue;
                if (merged.ContainsKey(b1.Id) || merged.ContainsKey(b2.Id))
                    continue;
                merged[b1.Id] = true;
                merged[b2.Id] = true;
                anyMerged = true;
                var level = b1.Level;
                if (level < levels - 1)
                    Spawn((b1.X + b2.X) * 0.5, (b1.Y + b2.Y) * 0.5, level + 1);
                score += (level + 1) * (level + 1);
                if (score > best)
                    best = score;
                Audio.Play(Sfx.Blip(400, 840, 0.12, 0.35),
                    new PlayOpts { Pitch = 1.0 + level * 0.15 });
            }
            if (anyMerged)
            {
                var kept = new List<Ball>();
                foreach (var ball in balls)
                {
                    if (!merged.ContainsKey(ball.Id))
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
                var top = ball.Y + radii[ball.Level];
                if (top > lineY && ball.Age > 1.0)
                    ball.OverT += dt;
                else
                    ball.OverT = 0.0;
                if (ball.OverT > 1.0)
                {
                    over = true;
                    Audio.Play(Sfx.Noise(0.4, 0.5, 0x2468ace));
                }
            }
        }

        // --- 描画
        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new double[] { 0.10, 0.09, 0.13, 1.0 },
        });
        batchNow.Begin();

        // 容器
        var wallCol = Color.Rgb(0.35, 0.32, 0.42);
        batchNow.Rect(camNow.Sx(-halfW) - 8, camNow.Sy(wallTop), 8, wallTop * ppm,
            wallCol);
        batchNow.Rect(camNow.Sx(halfW), camNow.Sy(wallTop), 8, wallTop * ppm,
            wallCol);
        batchNow.Rect(camNow.Sx(-halfW) - 8, camNow.OriginY, halfW * 2 * ppm + 16, 8,
            wallCol);

        // ゲームオーバー線
        var lineBlink = over ? 1.0 : 0.25 + 0.15 * Math.Sin(t * 4.0);
        batchNow.Rect(camNow.Sx(-halfW), camNow.Sy(lineY), halfW * 2 * ppm, 2,
            Color.Rgb(0.9, 0.3, 0.3, lineBlink));

        // 玉 (sprite は本体、上に mesh グリフ)
        foreach (var ball in balls)
        {
            var r = radii[ball.Level] * ppm;
            var c = colors[ball.Level];
            batchNow.Disc(camNow.Sx(ball.X), camNow.Sy(ball.Y), r, c);
        }

        // 投下プレビュー
        if (!over)
        {
            var r = dropR * ppm;
            var c = colors[nextLevel];
            batchNow.Disc(camNow.Sx(dropX), camNow.Sy(dropY), r,
                Color.Rgb(c.R, c.G, c.B, 0.5 + 0.2 * Math.Sin(t * 6.0)));
        }

        // HUD (bitmap 小サイズレジーム)
        hudNow.Draw(batchNow, "スコア " + score, 12, 26);
        hudNow.Draw(batchNow, "ベスト " + best, 12, 50,
            Color.Rgb(0.8, 0.8, 0.8, 0.8));
        hudNow.Draw(batchNow, "いろはにほへと", 500, 26,
            Color.Rgb(0.7, 0.7, 0.8, 0.9), 0.8);

        batchNow.Flush();

        // mesh グリフ (拡大レジーム): 玉の文字は物理の回転ごと描く
        var ink = Color.Rgb(0.12, 0.10, 0.14, 0.9);
        foreach (var ball in balls)
        {
            meshNow.Char(chars[ball.Level], camNow.Sx(ball.X), camNow.Sy(ball.Y),
                radii[ball.Level] * ppm * 1.3, ball.Angle, ink, true);
        }
        if (!over)
            meshNow.Char(chars[nextLevel], camNow.Sx(dropX), camNow.Sy(dropY),
                dropR * ppm * 1.3, 0.0,
                Color.Rgb(ink.R, ink.G, ink.B, 0.6), true);

        if (over)
        {
            // 帯とメッセージは玉の上に重ねたいので別 batch で flush を分ける
            overlayNow.Begin();
            overlayNow.Rect(0, 108, w, 132, Color.Rgb(0.05, 0.04, 0.07, 0.85));
            var msg = "クリックでもういちど";
            hudNow.Draw(overlayNow, msg,
                camNow.OriginX - hudNow.Width(msg) * 0.5, 222);
            overlayNow.Flush();
            meshNow.TextCentered("おしまい", camNow.OriginX, 190, 64,
                Color.Rgb(0.95, 0.92, 0.85));
        }

        Gfx.EndPass();
    }
}
