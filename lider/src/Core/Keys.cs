// SDL scancodes (EventData.Key). Characters depend on the keyboard layout, so they are received through EventData.Typed.
// lub's KeyDown also delivers key repeats while a key is held.
using TinySystem.Text;

namespace LiderCore;

public static class Keys
{
    // Bits of EventData.Mods
    public const int ModShift = 1;
    public const int ModCtrl = 2;
    public const int ModAlt = 4;

    public const int A = 4;
    public const int P = 19;
    public const int S = 22;
    public const int Digit1 = 30;
    public const int Return = 40;
    public const int Escape = 41;
    public const int Backspace = 42;
    public const int Tab = 43;
    public const int Space = 44;
    public const int F1 = 58;
    public const int F2 = 59;
    public const int F3 = 60;
    public const int F4 = 61;
    public const int F5 = 62;
    public const int F6 = 63;
    public const int F7 = 64;
    public const int F8 = 65;
    public const int F9 = 66;
    public const int F10 = 67;
    public const int F11 = 68;
    public const int F12 = 69;
    public const int Minus = 45;
    public const int Home = 74;
    public const int Delete = 76;
    public const int End = 77;
    public const int PageUp = 75;
    public const int PageDown = 78;
    public const int Right = 79;
    public const int Left = 80;
    public const int Down = 81;
    public const int Up = 82;
    public const int LeftCtrl = 224;
    public const int LeftShift = 225;
    public const int LeftAlt = 226;
    public const int RightCtrl = 228;
    public const int RightShift = 229;
    public const int RightAlt = 230;

    // Scancodes 30 to 56.
    // 40 to 43 (Return, Escape, Backspace, Tab) do not produce characters, so a 0 marker is placed there
    const string Plain = "1234567890\0\0\0\0 -=[]\\#;'`,./";
    const string Shifted = "!@#$%^&*()\0\0\0\0 _+{}|~:\"~<>?";

    // The character the key types on a US layout (0 for non-printable keys). Used by tests to type without a keyboard
    public static int UsChar(int key, bool shift)
    {
        if (key >= 4 && key <= 29)
            return (shift ? 'A' : 'a') + key - 4;
        if (key >= 30 && key <= 56)
        {
            int c = new Utf8Bytes(shift ? Shifted : Plain)[key - 30];
            return c >= 32 && c < 127 ? c : 0;
        }
        return 0;
    }
}
