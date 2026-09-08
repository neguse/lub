using System;
using static Lub;

public static class Tetris29
{
    static TetrisGame? game;
    static SpriteBatch? batch;
    static Text? font;
    static FixedStep? step;
    static int heldDirection, heldFrames, frame;
    static bool scenario;
    static int[] colors = new int[] { 0x52DCE8, 0xF3CE63, 0xAF8AFF, 0x6BDEAC, 0xF47E93, 0x6C9EF5, 0xF4A56B };

    public static void OnInit()
    {
        Config(new ConfigOpts { Width = 720, Height = 720, Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") });
        scenario = Environment.GetEnvironmentVariable("TETRIS_SCENARIO") == "clear";
    }

    public static void OnFrame(float dt)
    {
        game ??= new TetrisGame();
        batch ??= new SpriteBatch(720, 720);
        step ??= new FixedStep();
        const string path = "samples/29_tetris/data/MPLUS1p-subset.ttf";
        Io.LoadBytes(path, out var bytes, out _, out _, out _);
        if (bytes == null) return;
        font ??= new Text("tetris_font", path, 24);
        frame++;
        if (scenario)
        {
            // A game-owned fixture, not a runtime input/replay facility.
            if (frame == 1)
            {
                for (int y = 16; y < 20; y++)
                    for (int x = 0; x < 10; x++) game.Board[y * 10 + x] = x == 5 ? 0 : (x % 7) + 1;
                game.Piece = 0; game.Rotation = 0; game.X = 3; game.Y = 0;
                Console.WriteLine("TETRIS_SCENARIO ready");
            }
            if (frame == 10) game.Rotate(1);
            if (frame == 30)
            {
                game.Drop();
                Console.WriteLine("TETRIS_SCENARIO lines=" + game.Lines + " score=" + game.Score + " locked=" + game.Locked);
            }
        }
        else step.Frame(dt, tickDt => Update(tickDt));
        Draw();
    }

    static void Update(float dt)
    {
        var g = game!;
        var s = step!;
        if (s.KeyPressed("r")) { g.Reset(); heldDirection = 0; heldFrames = 0; return; }
        if (s.KeyPressed("p")) g.Paused = !g.Paused;
        if (g.Over || g.Paused) return;
        if (s.KeyPressed("up") || s.KeyPressed("x")) g.Rotate(1);
        if (s.KeyPressed("z")) g.Rotate(-1);
        int direction = Input.KeyDown("left") || s.KeyPressed("left") ? -1
            : Input.KeyDown("right") || s.KeyPressed("right") ? 1 : 0;
        if (direction != heldDirection) { heldDirection = direction; heldFrames = 0; }
        if (direction != 0)
        {
            if (heldFrames == 0 || (heldFrames >= 12 && (heldFrames - 12) % 4 == 0)) g.Move(direction, 0);
            heldFrames++;
        }
        if (s.KeyPressed("space")) { g.Drop(); return; }
        g.Tick(dt, Input.KeyDown("down"));
    }

    static void Label(string text, float x, float y, int color)
    {
        font!.Draw(batch!, text, x, y, Color.Hex(color));
    }

    static void Block(int piece, float x, float y, bool ghost)
    {
        int rgb = colors[piece];
        batch!.Rect(x + 1, y + 1, 28, 28, Color.Hex(rgb, ghost ? 0.18f : 1));
        batch.Rect(x + 3, y + 3, 24, 3, Color.Hex(0xFFFFFF, ghost ? 0.2f : 0.35f));
    }

    static void DrawPiece(int piece, int rotation, float x, float y, bool ghost)
    {
        for (int i = 0; i < 4; i++)
        {
            int c = game!.Cell(piece, rotation, i);
            Block(piece, x + c % 4 * 30, y + (int)Math.Floor(c / 4.0f) * 30, ghost);
        }
    }

    static void Draw()
    {
        var g = game!;
        var b = batch!;
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0.035f, 0.05f, 0.09f, 1 } });
        b.Begin();
        b.Rect(34, 56, 312, 612, Color.Hex(0x253449));
        b.Rect(40, 62, 300, 600, Color.Hex(0x0E1726));
        for (int y = 0; y < 20; y++)
            for (int x = 0; x < 10; x++)
            {
                b.Rect(41 + x * 30, 63 + y * 30, 28, 28, Color.Hex(0x152133));
                int cell = g.Board[y * 10 + x];
                if (cell != 0) Block(cell - 1, 40 + x * 30, 62 + y * 30, false);
            }
        if (!g.Over)
        {
            DrawPiece(g.Piece, g.Rotation, 40 + g.X * 30, 62 + g.GhostY() * 30, true);
            DrawPiece(g.Piece, g.Rotation, 40 + g.X * 30, 62 + g.Y * 30, false);
        }
        b.Rect(380, 145, 290, 135, Color.Hex(0x152133));
        DrawPiece(g.Next, 0, 405, 180, false);
        if (g.Over || g.Paused) b.Rect(55, 305, 270, 105, Color.Hex(0x09101E, 0.96f));
        b.Flush();
        // Text uses another atlas; flush backgrounds first to preserve layering.
        b.Begin();
        Label("LUB / BLOCKS", 380, 85, 0xE8EFF8);
        Label("NEXT", 398, 176, 0x8EABC7);
        Label("SCORE   " + g.Score, 380, 330, 0xE8EFF8);
        Label("LINES    " + g.Lines, 380, 370, 0xE8EFF8);
        Label("LEVEL    " + (g.Level() + 1), 380, 410, 0xE8EFF8);
        Label("LEFT / RIGHT   Move", 380, 478, 0x8EABC7);
        Label("UP / X / Z    Rotate", 380, 512, 0x8EABC7);
        Label("DOWN   Soft drop", 380, 546, 0x8EABC7);
        Label("SPACE   Hard drop", 380, 580, 0x8EABC7);
        Label("P   Pause    R   Restart", 380, 632, 0x8EABC7);
        Label("10 x 20 / SEVEN BAG", 40, 697, 0x68829E);
        if (g.Over || g.Paused)
        {
            Label(g.Over ? "GAME OVER" : "PAUSED", 102, 348, 0xE8EFF8);
            Label(g.Over ? "R to restart" : "P to resume", 105, 385, 0x8EABC7);
        }
        b.Flush();
        Gfx.EndPass();
    }
}
