// tcs VM の player (lub_vm Game.csproj) の overlay の OnFrame の失敗の検査 (scripts/vm-project-test.sh が一時ディレクトリに写して動かす)。
// overlay の OnFrame が失敗した frame は、ゲームの OnFrame が失敗した frame と同じく、宣言した resource の sweep までの frame に数えない。
// 1 frame 目に buffer を宣言し、sweep までの frame 数 (2) より多く overlay を失敗させても、buffer は残る。
using System;
using System.Collections.Generic;
using Lub;

public static class Tool
{
    static int frames;

    public static void OnFrame(float dt)
    {
        frames++;
        if (frames == 1)
        {
            Gfx.UseBuffer("tool-state", Gfx.BufferType.Storage, new List<float> { 1 }, 1);
            return;
        }
        if (frames <= 5)
        {
            Io.SaveText("", "intentional error");
            return;
        }
        if (Gfx.LookupBuffer("tool-state") == null)
        {
            Console.WriteLine("FAIL the buffer was swept during the failed overlay frames");
            App.Quit(1);
            return;
        }
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    public static ErrorAction OnError(string message) { return ErrorAction.Log; }
}
