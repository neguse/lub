// web の Gamepad API の pad が Input.Gamepad* に届くこと。
// pad は web の verify (A9) が navigator.getGamepads を差し替えて用意する (native では走らせない)。
// 差し替えた pad は standard mapping で、接続の少し後に、左 stick を左に倒し、South と右 trigger と D-pad の上を押す。
using System;
using Lub;

public static class TestGamepad
{
    static int frame;

    public static void OnFrame(float dt)
    {
        Check.Frame();
        frame++;
        if (frame > 600)
        {
            Check.Fail("South was not pressed within 600 frames (connected=" + Input.GamepadConnected(0) + ")");
            return;
        }
        if (!Input.GamepadPressed(0, Input.PadButton.South))
            return;
        Check.That(Input.GamepadDown(0, Input.PadButton.South), "South must be down in the frame it is pressed");
        Check.That(Input.GamepadConnected(0) && !Input.GamepadConnected(1), "the pad must be in slot 0 only");
        Check.That(Math.Abs(Input.GamepadAxis(0, Input.PadAxis.LeftX) + 1) < 0.001f, "LeftX must be -1, got " + Input.GamepadAxis(0, Input.PadAxis.LeftX));
        Check.That(Math.Abs(Input.GamepadAxis(0, Input.PadAxis.RightTrigger) - 1) < 0.001f, "RightTrigger must be 1, got " + Input.GamepadAxis(0, Input.PadAxis.RightTrigger));
        Check.That(Input.GamepadDown(0, Input.PadButton.DpadUp), "DpadUp must be down");
        Check.That(!Input.GamepadDown(0, Input.PadButton.East), "East must be up");
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
