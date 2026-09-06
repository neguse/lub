// lub の samples/21_iroha の entry。
// 実行: lub samples/21_iroha/Iroha21.csproj (transpile + watch + hot reload)
// いろはスイカ: 同じ文字の玉がぶつかると「い→ろ→は→に→ほ→へ→と」の
// 順に育つスイカゲーム風サンプル。玉の文字は font_glyph_mesh の
// 三角形化グリフ (MeshText)、HUD は lubx.Text の動的 glyph atlas。
// 玉 1 個は class Ball で持ち、
// contact の body key 逆引きは substr + parseInt でなく Dictionary<string, Ball>
// で行う (機能は同一)。
// cs-lib のクラス (Color / Camera2d / SpriteBatch / Rand) は生成 Lua で
// サンプルより後に定義されるため、static 初期化子で参照できない
// (load 時 nil)。それらは onFrame 先頭で遅延生成する。

using System;
using System.Collections.Generic;
using static Lub;

/// <summary>玉 1 個。</summary>
public class Ball
{
    public int Id;
    public int Level;
    public float SpawnX; // 宣言用: initial は不変でないと body が作り直される
    public float SpawnY;
    public float X;
    public float Y;
    public float Angle;
    public float Age;
    public float OverT;
}

public static class Iroha21
{
    const int w = 640;
    const int h = 360;
    const float ppm = 100.0f; // physics m -> logical px
    const float halfW = 1.15f; // 容器の半幅 (m)
    const float wallTop = 3.2f; // 壁の上端 (m)
    const float lineY = 2.45f; // ゲームオーバー線 (m)
    const float dropY = 2.85f; // 投下位置 (m)
    const int levels = 7;

    static string[] chars = new string[]
        { "い", "ろ", "は", "に", "ほ", "へ", "と" };
    static float[] radii = new float[]
        { 0.13f, 0.17f, 0.22f, 0.28f, 0.36f, 0.46f, 0.58f };
    static Color[]? _colors = null; // onFrame 先頭で遅延生成

    static Camera2d? cam = null;

    static SpriteBatch? batch = null;
    // ゲームオーバー表示用。SpriteBatch は atlas バケツ順で描くので、玉の上に
    // 帯を重ねるには flush を分ける必要がある (バッファも別 prefix にする)。
    static SpriteBatch? overlay = null;
    static Text? hud = null;
    static MeshText? mesh = null;
    const string fontPath = "samples/21_iroha/data/MPLUS1p-subset.ttf";
    static bool fontLoaded = false;
    static int fontVersion = 0;

    static List<Ball> balls = new List<Ball>();
    static int nextId = 0;
    static int nextLevel = 0;
    static int score = 0;
    static int best = 0;
    static float cooldown = 0.0f;
    static bool over = false;
    static float t = 0.0f;
    static Rand? rng = null;

    // LUB_IROHA_AUTO=1 で自動プレイ (ヘッドレス検証・デモ用)
    static bool auto = Environment.GetEnvironmentVariable("LUB_IROHA_AUTO") != null;

    public static void OnInit()
    {
        var backend = Environment.GetEnvironmentVariable("LUB_BACKEND");
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
        Io.LoadBytes(fontPath, out var bytes, out var version, out _, out _);
        if (bytes == null) return false;
        if (!fontLoaded || fontVersion != version)
        {
            fontLoaded = true;
            fontVersion = version;
            hud = new Text("iroha_hud", fontPath, 20);
            mesh = new MeshText("iroha_mesh", fontPath, fontVersion, w, h);
        }
        return fontLoaded && mesh != null;
    }

    // --- ゲーム -------------------------------------------------------------

    static void Spawn(float x, float y, int level)
    {
        balls.Add(new Ball
        {
            Id = nextId,
            Level = level,
            SpawnX = x,
            SpawnY = y,
            X = x,
            Y = y,
            Angle = 0.0f,
            Age = 0.0f,
            OverT = 0.0f,
        });
        nextId++;
    }

    static void Reset()
    {
        balls = new List<Ball>();
        score = 0;
        over = false;
        cooldown = 0.3f;
    }

    public static void OnFrame(float dt)
    {
        t += dt;
        if (dt > 0.1f)
            dt = 0.1f;
        if (!EnsureAssets()) return;
        var hudNow = hud;
        var meshNow = mesh;
        if (hudNow == null || meshNow == null) return;

        var camNow = cam ?? new Camera2d(w, h, ppm, 320.0f, 344.0f);
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
                Color.Rgb(0.91f, 0.36f, 0.36f),
                Color.Rgb(0.93f, 0.60f, 0.34f),
                Color.Rgb(0.93f, 0.83f, 0.36f),
                Color.Rgb(0.49f, 0.80f, 0.42f),
                Color.Rgb(0.36f, 0.72f, 0.91f),
                Color.Rgb(0.50f, 0.45f, 0.93f),
                Color.Rgb(0.83f, 0.36f, 0.91f),
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
        if (auto && cooldown <= 0.0f && !over)
        {
            click = true;
            dropX = (rngNow.NextFloat() * 2.0f - 1.0f) * (halfW - dropR);
        }
        if (over)
        {
            if (click)
                Reset();
        }
        else if (click && cooldown <= 0.0f)
        {
            Spawn(dropX, dropY, nextLevel);
            var pickTable = new int[] { 0, 0, 0, 1, 1, 2 };
            nextLevel = pickTable[(int)Math.Floor(rngNow.NextFloat() * 6.0f)];
            cooldown = 0.45f;
            Audio.Play(Sfx.Blip(420, 260, 0.06f, 0.3f));
        }

        // --- 物理 (immediate mode: 生きている玉だけ毎フレーム宣言する)
        var world = Phys2d.World("iroha", new WorldOpts
        {
            Gravity = new Vec2d { X = 0.0f, Y = -10.0f },
            FixedDt = 1.0f / 120.0f,
            Substeps = 4,
            MaxSteps = 4,
        });
        if (world == null) return;
        Phys2d.Begin(world);

        var arena = Phys2d.Body(world, "arena", new BodyDesc
        {
            Type = Phys2d.BodyType.Static,
            Initial = new InitialState { X = 0.0f, Y = 0.0f },
        });
        if (arena == null) return;
        Phys2d.Box(arena, "floor", new BoxDesc
        {
            Hx = halfW + 0.3f,
            Hy = 0.1f,
            Cy = -0.1f,
            Friction = 0.5f,
        });
        Phys2d.Box(arena, "wall_l", new BoxDesc
        {
            Hx = 0.1f,
            Hy = wallTop * 0.5f,
            Cx = -(halfW + 0.1f),
            Cy = wallTop * 0.5f,
            Friction = 0.3f,
        });
        Phys2d.Box(arena, "wall_r", new BoxDesc
        {
            Hx = 0.1f,
            Hy = wallTop * 0.5f,
            Cx = halfW + 0.1f,
            Cy = wallTop * 0.5f,
            Friction = 0.3f,
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
                Density = 1.0f,
                Friction = 0.35f,
                Restitution = 0.12f,
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
                    Spawn((b1.X + b2.X) * 0.5f, (b1.Y + b2.Y) * 0.5f, level + 1);
                score += (level + 1) * (level + 1);
                if (score > best)
                    best = score;
                Audio.Play(Sfx.Blip(400, 840, 0.12f, 0.35f),
                    new PlayOpts { Pitch = 1.0f + level * 0.15f });
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
                if (top > lineY && ball.Age > 1.0f)
                    ball.OverT += dt;
                else
                    ball.OverT = 0.0f;
                if (ball.OverT > 1.0f)
                {
                    over = true;
                    Audio.Play(Sfx.Noise(0.4f, 0.5f, 0x2468ace));
                }
            }
        }

        // --- 描画
        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new float[] { 0.10f, 0.09f, 0.13f, 1.0f },
        });
        batchNow.Begin();

        // 容器
        var wallCol = Color.Rgb(0.35f, 0.32f, 0.42f);
        batchNow.Rect(camNow.Sx(-halfW) - 8, camNow.Sy(wallTop), 8, wallTop * ppm,
            wallCol);
        batchNow.Rect(camNow.Sx(halfW), camNow.Sy(wallTop), 8, wallTop * ppm,
            wallCol);
        batchNow.Rect(camNow.Sx(-halfW) - 8, camNow.OriginY, halfW * 2 * ppm + 16, 8,
            wallCol);

        // ゲームオーバー線
        var lineBlink = over ? 1.0f : 0.25f + 0.15f * (float)Math.Sin(t * 4.0f);
        batchNow.Rect(camNow.Sx(-halfW), camNow.Sy(lineY), halfW * 2 * ppm, 2,
            Color.Rgb(0.9f, 0.3f, 0.3f, lineBlink));

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
                Color.Rgb(c.R, c.G, c.B, 0.5f + 0.2f * (float)Math.Sin(t * 6.0f)));
        }

        // HUD (bitmap 小サイズレジーム)
        hudNow.Draw(batchNow, "スコア " + score, 12, 26);
        hudNow.Draw(batchNow, "ベスト " + best, 12, 50,
            Color.Rgb(0.8f, 0.8f, 0.8f, 0.8f));
        hudNow.Draw(batchNow, "いろはにほへと", 500, 26,
            Color.Rgb(0.7f, 0.7f, 0.8f, 0.9f), 0.8f);

        batchNow.Flush();

        // mesh グリフ (拡大レジーム): 玉の文字は物理の回転ごと描く
        var ink = Color.Rgb(0.12f, 0.10f, 0.14f, 0.9f);
        foreach (var ball in balls)
        {
            meshNow.Char(chars[ball.Level], camNow.Sx(ball.X), camNow.Sy(ball.Y),
                radii[ball.Level] * ppm * 1.3f, ball.Angle, ink, true);
        }
        if (!over)
            meshNow.Char(chars[nextLevel], camNow.Sx(dropX), camNow.Sy(dropY),
                dropR * ppm * 1.3f, 0.0f,
                Color.Rgb(ink.R, ink.G, ink.B, 0.6f), true);

        if (over)
        {
            // 帯とメッセージは玉の上に重ねたいので別 batch で flush を分ける
            overlayNow.Begin();
            overlayNow.Rect(0, 108, w, 132, Color.Rgb(0.05f, 0.04f, 0.07f, 0.85f));
            var msg = "クリックでもういちど";
            hudNow.Draw(overlayNow, msg,
                camNow.OriginX - hudNow.Width(msg) * 0.5f, 222);
            overlayNow.Flush();
            meshNow.TextCentered("おしまい", camNow.OriginX, 190, 64,
                Color.Rgb(0.95f, 0.92f, 0.85f));
        }

        Gfx.EndPass();
    }
}
