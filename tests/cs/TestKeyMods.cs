// On web, a key from an environment where CapsLock is mapped to Ctrl (key is "Control", code is "CapsLock") reaches EventData.Mods as Ctrl.
// The keys are prepared by the web verify (A9), which dispatches KeyboardEvents (not run on native).
// It dispatches a keydown of that Ctrl, a keydown of "s" (with ctrlKey), and keyups of "s" and Ctrl.
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
