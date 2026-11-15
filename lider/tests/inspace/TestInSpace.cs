// GameView のウィンドウを、Screen の並びから外して Space に置く。
//   - Alt を押している間は、ゲームの絵が 1 枚だけでも見出しの帯が出て、帯の印を押すと Space に出る
//   - Space に置いたゲームは、どの Screen の並びにも居ないが、進み続け、画面いっぱいに描かれる
//   - Alt を押している間に出る Space の札を押すと、元の Screen の同じ順番の所に戻る
//   - 配置を控えると Space に置いた物も file に書かれ、その配置に戻すと Space に置かれる
using System;
using System.Collections.Generic;
using Lub;
using LiderCore;

public static class TestInSpace
{
    static int frames;
    static int stage;
    static float waited;
    static bool failed;
    static int framesBefore;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 1280, Height = 720 });
        Lider.OnInit();
    }

    public static void OnEvent(EventData e)
    {
        Lider.OnEvent(e);
    }

    public static void OnQuit()
    {
        Lider.OnQuit();
    }

    public static void OnFrame(float dt)
    {
        TestFrame(dt);
        Lider.OnFrame(dt);
    }

    static void TestFrame(float dt)
    {
        // ゲームの build を待つ
        if (failed || GameApp.Frames == 0)
            return;
        frames++;
        var w = Lider.Work!;
        var backdrop = Lider.Screens[0];
        if (frames < 6)
            return;
        waited += dt;
        if (waited < 0.4f)
            return;
        waited = 0;
        switch (stage++)
        {
            case 0:
                Check(backdrop.Windows[0].Content is GameView && w.InSpace == null, "the game is not on the backdrop at first");
                w.SetMods(Keys.ModAlt);
                break;
            case 1:
                var game = backdrop.Windows[0];
                var bar = game.ViewportRect;
                Check(bar != null && game.TitleBar > 0, "the title bar is not out while Alt is down");
                if (failed)
                    return;
                // 帯の右の端の印 (保存した版には閉じる印が無いので、右の端が Space へ出す印)
                Press(bar!.X + bar.W - game.TitleBar * 0.5f, bar.Y + game.TitleBar * 0.5f);
                Check(w.InSpace == game && w.ScreenOf(game) == null, "pressing the mark did not put the game in the space");
                w.SetMods(0);
                framesBefore = GameApp.Frames;
                break;
            case 2:
                var spaced = w.InSpace!;
                Check(GameApp.Frames > framesBefore, "the game stopped in the space");
                Check(spaced.ContentRect != null && spaced.ContentRect.X == 0 && Math.Abs(spaced.ContentRect.W - 1280) <= 1 && Math.Abs(spaced.ContentRect.H - 720) <= 1, "the game in the space does not fill the viewport");
                Check(GameApp.Map.W == (int)(1280 * Lider.PixelRatio), "the picture is " + GameApp.Map.W + " px wide");
                Check(Lider.SpaceTagOf() == null, "the space tag is out without Alt");
                // 配置を控えると、Space に置いた物も書かれる
                Lider.SaveArrangement("spaced");
                Io.LoadText(w.Root + "/.lider/arrangements.txt", out var text, out _, out _, out _);
                Check(text != null && text.Contains("arrangement spaced\nspace game:saved\n") && text.Contains("window game:saved\n"), "the file: " + text);
                var read = text != null ? Arrangement.Read(text) : new List<Arrangement>();
                Check(read.Count == 1 && read[0].Space == "game:saved" && string.Join("\n", read[0].Lines()) + "\n" == text, "reading the file back gave different lines");
                w.SetMods(Keys.ModAlt);
                break;
            case 3:
                var tag = Lider.SpaceTagOf();
                Check(tag != null, "the space tag is not out while Alt is down");
                if (failed)
                    return;
                var at = Lider.SurfacePx(backdrop, tag!.Value.X, tag.Value.Y);
                var was = w.InSpace!;
                Press(at.X, at.Y);
                Check(w.InSpace == null && backdrop.Windows[0] == was, "pressing the space tag did not put the game back first on the backdrop");
                w.SetMods(0);
                break;
            case 4:
                var back = backdrop.Windows[0];
                Check(back.ContentRect != null && Math.Abs(back.ContentRect.W - 640) <= 1, "the game is not the left half again");
                // 控えた配置に戻すと、Space に置かれる
                Check(Lider.Arrange("spaced") && w.InSpace == back && w.ScreenOf(back) == null, "the arrangement did not put the game in the space");
                Check(Lider.Arrange(Lider.SideBySide) && w.InSpace == null && backdrop.Windows[0] == back, "side-by-side did not take the game out of the space");
                if (!failed)
                {
                    Console.WriteLine("PASS");
                    App.Quit(0);
                }
                break;
        }
    }

    static void Press(float x, float y)
    {
        Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonDown, Button = 1, X = x, Y = y });
        Lider.OnEvent(new EventData { Kind = EventKind.MouseButtonUp, Button = 1, X = x, Y = y });
    }

    static void Check(bool ok, string message)
    {
        if (ok || failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }
}
