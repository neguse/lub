// web で CapsLock を Ctrl にした環境の key (key は "Control"、code は "CapsLock") が、Ctrl として EventData.Mods に届くこと。
// key は web の verify (A9) が KeyboardEvent を流して用意する (native では走らせない)。
// 流すのは、その Ctrl の keydown、"s" の keydown (ctrlKey 付き)、"s" と Ctrl の keyup。
using System;
using Lub;

public static class TestKeyMods
{
    static int frame;
    static int sMods = -1;

    public static void OnEvent(EventData e)
    {
        if (e.Kind == EventKind.KeyDown && e.Key == 22)
            sMods = e.Mods;
    }

    public static void OnFrame(float dt)
    {
        Check.Frame();
        frame++;
        if (frame > 600)
        {
            Check.Fail("S was not pressed within 600 frames");
            return;
        }
        if (sMods < 0)
            return;
        Check.That((sMods & 2) != 0, "S pressed with the remapped Ctrl has Mods " + sMods + ", want Ctrl (2)");
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
