// overlay (csproj の ProjectReference に Lub="Overlay" を付けた参照先の entry class) の呼び順。
// lub tests/cs/TestOverlay.csproj で動かす (overlay は csproj の entry でだけ付く)。
// OnInit と OnFrame はゲームの後に overlay が呼ばれる。
// ゲームの OnFrame が pass を開いたまま失敗した frame でも、overlay の OnFrame は自分の pass を開ける。
// OnEvent と OnQuit の順と Input.Grab は、event を送れる tests/c/overlay_event_smoke で見る。
using System;
using Lub;

public static class TestOverlay
{
    public static int Inits;
    public static int Frames;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "vulkan" });
        Check.That(TestOverlayTool.Inits == 0, "the overlay OnInit ran before the game OnInit");
        Inits++;
        Input.Grab(true, true);
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        Check.That(TestOverlayTool.Frames == Frames, "the overlay OnFrame ran before the game OnFrame");
        Frames++;
        if (Frames == 2)
        {
            Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex });
            Check.Error("a failing call inside a pass", "invalid path");
            Io.SaveText("", "x");
        }
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
