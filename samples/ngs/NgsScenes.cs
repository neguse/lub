using static Lub;

public enum NgsTransitionKind
{
    Stay = 0,
    Switch = 1,
    Quit = 2,
}

/// <summary>scene の遷移。Switch は次の scene を持つ。</summary>
public class NgsTransition
{
    public NgsTransitionKind Kind;
    public INgsScene? Scene;

    public NgsTransition(NgsTransitionKind kind, INgsScene? scene)
    {
        Kind = kind;
        Scene = scene;
    }

    public static NgsTransition Stay() => new NgsTransition(NgsTransitionKind.Stay, null);

    public static NgsTransition Switch(INgsScene s) => new NgsTransition(NgsTransitionKind.Switch, s);

    public static NgsTransition Quit() => new NgsTransition(NgsTransitionKind.Quit, null);
}

public interface INgsScene
{
    void Update(NgsInputSnapshot input);
    void Draw(NgsDrawList dl);
    NgsTransition Transition();
}

public class NgsTitle : INgsScene
{
    public const int SelStart = 0;
    public const int SelEnd = 1;

    int sel = SelStart;
    int anim = 0;
    int drawAnim = 0;
    NgsTransition next = NgsTransition.Stay();
    bool noGodHeld = false;

    public void Update(NgsInputSnapshot input)
    {
        // 従来の draw 後 increment と同じ位相を fixed tick 側で保つ。
        drawAnim = anim;
        anim = anim + 1;
        if (input.DirY < 0) sel = SelStart; // up
        else if (input.DirY > 0) sel = SelEnd; // down
        noGodHeld = input.NoGod;
        next = NgsTransition.Stay();
        if (input.Menu)
        {
            if (sel == SelStart) next = NgsTransition.Switch(new NgsPlay(input.NoGod, false, false));
            else next = NgsTransition.Quit();
        }
        else if (input.Cancel)
        {
            next = NgsTransition.Quit();
        }
    }

    public void Draw(NgsDrawList dl)
    {
        var white = Color.Rgb(1, 1, 1, 1);
        var title = noGodHeld ? "no god shooting game" : "no good shooting game";
        NgsGame.Font!.DrawString(dl, 220, 80, title, white);
        NgsGame.Font.DrawString(dl, 315, 90, "presented by ngs 2004", white);
        NgsGame.Font.DrawString(dl, 220, 390, "start", white);
        NgsGame.Font.DrawString(dl, 220, 420, "end", white);

        int frame = (drawAnim >> 3) & 3;
        int cursorY = sel == SelStart ? 390 : 420;
        dl.Sprite(NgsGame.CursorAtlas!, NgsAtlases.Cursor[frame], 210, cursorY);
    }

    public NgsTransition Transition() => next;
}

public class NgsPlay : INgsScene
{
    readonly NgsWorld world;
    readonly NgsSpawner spawner;
    int timer = 0;
    int phase = 0; // 0=intro, 1=active
    readonly bool skipIntro;

    public NgsPlay(bool noGod, bool bossOnly, bool skipIntro)
    {
        NgsGame.Score = 0;
        world = new NgsWorld(noGod);
        spawner = new NgsSpawner(noGod, bossOnly);
        this.skipIntro = skipIntro || bossOnly;
        if (this.skipIntro) phase = 1;
    }

    public void Update(NgsInputSnapshot input)
    {
        timer = timer + 1;
        if (phase == 0)
        {
            if (timer > 0xf0)
            {
                phase = 1;
                timer = 0;
            }
            return;
        }
        world.Tick(input);
        world.ResolveCollisions();
        spawner.Tick(world);
    }

    public void Draw(NgsDrawList dl)
    {
        var white = Color.Rgb(1, 1, 1, 1);
        if (phase == 0)
        {
            NgsGame.Font!.DrawString(dl, 0x11e - 200, 100, "n", white);
            if (timer > 0x3c) NgsGame.Font.DrawString(dl, 0x13c - 200, 100, "g", white);
            if (timer > 0x78) NgsGame.Font.DrawString(dl, 0x15a - 200, 100, "s", white);
            if (timer > 0xb4) NgsGame.Font.DrawString(dl, 0x12e - 200, 300, "g.o.!", white);
            DrawHud(dl, white);
            return;
        }
        // warning: 原典 game_timer 900..1100 で playfield 内を下スクロール (flavor)
        if (timer > 900 && timer < 0x44c)
        {
            NgsGame.Font!.DrawString(dl, 0x10a - 200, timer - 900, "w a r n i n g", white);
        }
        DrawHud(dl, white);
        world.DrawAll(dl);
    }

    void DrawHud(NgsDrawList dl, Color white)
    {
        NgsGame.Font!.DrawString(dl, 0x1cc - 200, 0x1e, "score:", white);
        NgsGame.Font.DrawString(dl, 0x1cc - 200, 0x32, NgsGame.Score.ToString(), white);
        NgsGame.Font.DrawString(dl, 0x1cc - 200, 0x50, "hi score:", white);
        NgsGame.Font.DrawString(dl, 0x1cc - 200, 100, NgsGame.Hiscore.ToString(), white);
        NgsGame.Font.DrawString(dl, 0x1cc - 200, 400, "life", white);
        for (int i = 0; i < world.Player.Lives; i++)
        {
            dl.Sprite(NgsGame.JikiAtlas!, NgsAtlases.Jiki[0], 0x1cc - 200 + 40 + i * 18, 400);
        }
    }

    public NgsTransition Transition()
    {
        if (world.BossDefeated || world.Player.IsFinished())
            return NgsTransition.Switch(new NgsGameOver(NgsGame.Score));
        return NgsTransition.Stay();
    }
}

// 全滅 or ボス撃破後の画面。score / hi-score 表示、Z 押下または数秒で Title へ。
public class NgsGameOver : INgsScene
{
    readonly int score;
    int t = 0;
    bool done = false;

    public const int Timeout = 300; // 5 秒で自動的に Title へ

    public NgsGameOver(int score)
    {
        this.score = score;
        if (score > NgsGame.Hiscore) NgsGame.Hiscore = score;
    }

    public void Update(NgsInputSnapshot input)
    {
        t = t + 1;
        if (input.Menu) done = true; // Z trigger
    }

    public void Draw(NgsDrawList dl)
    {
        var white = Color.Rgb(1, 1, 1, 1);
        NgsGame.Font!.DrawString(dl, 264, 200, "game over", white);
        NgsGame.Font.DrawString(dl, 264, 230, "score", white);
        NgsGame.Font.DrawInt(dl, 320, 230, score, 6, white);
        NgsGame.Font.DrawString(dl, 264, 245, "hi score", white); // 原典ラベル準拠 (ハイフン無し)
        NgsGame.Font.DrawInt(dl, 336, 245, NgsGame.Hiscore, 6, white);
    }

    public NgsTransition Transition() =>
        (done || t > Timeout) ? NgsTransition.Switch(new NgsTitle()) : NgsTransition.Stay();
}
