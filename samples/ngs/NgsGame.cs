using System;
using System.Collections.Generic;
using static Lub;

/// <summary>ゲーム全体の状態と frame の骨格。fixed step で scene を更新し、
/// 最後に更新した scene を描く。</summary>
public static class NgsGame
{
    public const int W = 640;
    public const int H = 480;

    public static NgsGfx2d? Gfx2d = null;
    public static Atlas? FontAtlas = null;
    public static Atlas? JikiAtlas = null;
    public static Atlas? CursorAtlas = null;
    public static Atlas? EnemyAtlas = null;
    public static NgsFont? Font = null;
    static INgsInputSource? input = null;
    static INgsScene? scene = null;
    static FixedStep? step = null;
    public static int FrameCount = 0;
    public static int Score = 0;
    public static int Hiscore = 0;

    public static void Init()
    {
        Config(new ConfigOpts { Width = W, Height = H });
    }

    static TextureOpts PixelArt()
    {
        return new TextureOpts { Filter = Gfx.Filter.Nearest, Wrap = Gfx.Wrap.Clamp };
    }

    static bool Boot()
    {
        NgsAtlases.Init();
        if (Gfx2d == null) Gfx2d = new NgsGfx2d();
        if (FontAtlas == null) FontAtlas = Atlas.FromPng("ngs_font", "samples/ngs/data/font.png", PixelArt());
        if (JikiAtlas == null) JikiAtlas = Atlas.FromPng("ngs_jiki", "samples/ngs/data/jiki.png", PixelArt());
        if (CursorAtlas == null) CursorAtlas = Atlas.FromPng("ngs_cursor", "samples/ngs/data/cursor.png", PixelArt());
        if (EnemyAtlas == null) EnemyAtlas = Atlas.FromPng("ngs_enemy", "samples/ngs/data/enemy.png", PixelArt());
        if (!Gfx2d.Ensure() || !FontAtlas.Ensure() || !JikiAtlas.Ensure() || !CursorAtlas.Ensure() || !EnemyAtlas.Ensure())
            return false;
        if (Font == null) Font = new NgsFont(FontAtlas);
        if (step == null) step = new FixedStep();
        if (input == null)
        {
            var mock = Environment.GetEnvironmentVariable("LUB_NGS_MOCK");
            if (mock == "fire")
            {
                input = new NgsMockInput(f =>
                {
                    var s = new NgsInputSnapshot();
                    if ((f & 1) == 0)
                    {
                        s.Fire = true;
                        s.Menu = true;
                    }
                    return s;
                });
            }
            else if (mock == "kill")
            {
                // 左へ 6 frame 寄り enemy#1 (spawn x280) の弾ライン上に陣取り手連射。
                // 連続弾の壁に降下してきた敵が即撃破され explosion が出る (golden 用)。
                input = new NgsMockInput(f =>
                {
                    var s = new NgsInputSnapshot();
                    if ((f & 1) == 0)
                    {
                        s.Fire = true;
                        s.Menu = true;
                    }
                    if (f < 6) s.DirX = -1;
                    return s;
                });
            }
            else if (mock != null)
            {
                input = new NgsMockInput(null);
            }
            else
            {
                input = new NgsInput();
            }
        }
        if (scene == null)
        {
            var boot = Environment.GetEnvironmentVariable("LUB_NGS_BOOT");
            if (boot == "play") scene = new NgsPlay(false, false, false);
            else if (boot == "active") scene = new NgsPlay(false, false, true); // intro skip (golden/debug 用)
            else if (boot == "boss") scene = new NgsPlay(false, true, false); // boss 直入り (golden 用)
            else if (boot == "gameover") scene = new NgsGameOver(12345); // score inject 直入り (golden 用)
            else scene = new NgsTitle();
        }
        return true;
    }

    static bool ApplyTransition(INgsScene from)
    {
        var t = from.Transition();
        if (t.Kind == NgsTransitionKind.Stay) return true;
        if (t.Kind == NgsTransitionKind.Switch)
        {
            scene = t.Scene;
            return true;
        }
        Quit();
        return false;
    }

    public static void Frame(float dt)
    {
        if (!Boot()) return;

        input!.Capture();
        var lastDrawScene = scene!;
        INgsScene? transitionScene = null;
        step!.Frame(dt, _ =>
        {
            // catch-up 中間の scene は、省略された draw の後と同じ位置で遷移する。
            if (transitionScene != null)
            {
                var from = transitionScene;
                transitionScene = null;
                if (!ApplyTransition(from))
                {
                    step.Stop();
                    return;
                }
            }

            input.Refresh();
            var updatedScene = scene!;
            updatedScene.Update(input.Current());
            lastDrawScene = updatedScene;
            transitionScene = updatedScene;
            FrameCount = FrameCount + 1;
        });

        Gfx2d!.BeginFrame();
        lastDrawScene.Draw(Gfx2d.DrawList!);
        Gfx2d.EndFrame();

        // 最後の fixed update は従来どおり update → draw → transition。
        if (transitionScene != null) ApplyTransition(transitionScene);
    }
}

/// <summary>敵の出現表。frame 番号で原典どおりに spawn する。</summary>
public class NgsSpawner
{
    int frame = 0;
    readonly bool noGod;
    readonly bool bossOnly; // golden 用: 通常面を飛ばし boss を frame 1 で出す

    public NgsSpawner(bool noGod, bool bossOnly)
    {
        this.noGod = noGod;
        this.bossOnly = bossOnly;
    }

    void Normal(NgsWorld world, int sx)
    {
        world.Spawn(NgsFaction.Enemies, new NgsNormal(sx, 0, world.Player.X + 8, world.Player.Y + 8, noGod));
    }

    void Wave(NgsWorld world, int sx)
    {
        world.Spawn(NgsFaction.Enemies, new NgsWave(sx, 0, noGod));
    }

    void Boss(NgsWorld world)
    {
        world.Spawn(NgsFaction.Enemies, new NgsBoss(320, -40, noGod));
    }

    public void Tick(NgsWorld world)
    {
        frame = frame + 1;
        if (bossOnly)
        {
            if (frame == 1) Boss(world);
            return;
        }
        switch (frame)
        {
            case 60:
                Normal(world, 280);
                break;
            case 120:
                Normal(world, 350);
                break;
            case 180:
                Wave(world, 300);
                break;
            case 300:
                Wave(world, 320);
                break;
            case 400:
                Normal(world, 280);
                Normal(world, 360);
                break;
            case 500:
                Wave(world, 300);
                Wave(world, 340);
                break;
            case 700:
                Boss(world);
                break;
            default:
                break;
        }
    }
}
