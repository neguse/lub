// On-screen keyboard: pressing a key in the layout produces the same key events as a connected keyboard.
// Shift, Ctrl and Alt apply to the next keystroke when pressed (pressing again releases them).
// The layout is US, and every row is Units wide.
using System.Collections.Generic;
using Lub;

namespace LiderCore;

public class OskKey
{
    // SDL scancode (0 for modifier keys)
    public int Key;
    // For a modifier key, its bit (Keys.ModShift etc.)
    public int Mod;
    // Label of a non-character key (empty for character keys, which show the US layout character)
    public string Name = "";
    // Width (1 = one character key)
    public float W = 1;
}

public class Osk : IContent
{
    public const int Units = 15;
    public readonly List<List<OskKey>> Rows = new List<List<OskKey>>();
    public bool Shown;
    // Modifier keys applied to the next keystroke (sum of Keys.Mod* bits)
    public int Mods;
    // Pointed key (row and index within the row; -1 if none). Held per pointer (left hand 0, right hand or mouse 1)
    public readonly int[] HoverRow = new int[] { -1, -1 };
    public readonly int[] HoverAt = new int[] { -1, -1 };
    // The drawn picture (drawn by OskView)
    public TextureRef? Picture;

    public Osk()
    {
        var top = new List<OskKey> { Named(Keys.Escape, "Esc", 1.5f), Named(Keys.F1, "F1"), Named(Keys.F5, "F5"), Named(Keys.F6, "F6"), Named(Keys.F7, "F7"),
            Named(Keys.Home, "Home"), Named(Keys.End, "End"), Named(Keys.PageUp, "PgUp"), Named(Keys.PageDown, "PgDn"), Named(Keys.Delete, "Del", 1.5f),
            Named(Keys.Left, "←"), Named(Keys.Down, "↓"), Named(Keys.Up, "↑"), Named(Keys.Right, "→") };
        Rows.Add(top);
        // ` 1234567890 - =
        var digits = new List<OskKey> { Char(53) };
        for (var i = 0; i < 10; i++)
            digits.Add(Char(Keys.Digit1 + i));
        digits.Add(Char(Keys.Minus));
        digits.Add(Char(46));
        digits.Add(Named(Keys.Backspace, "BS", 2));
        Rows.Add(digits);
        var upper = new List<OskKey> { Named(Keys.Tab, "Tab", 1.5f) };
        Letters(upper, new int[] { 'q', 'w', 'e', 'r', 't', 'y', 'u', 'i', 'o', 'p' });
        upper.Add(Char(47));
        upper.Add(Char(48));
        upper.Add(new OskKey { Key = 49, W = 1.5f });
        Rows.Add(upper);
        var home = new List<OskKey> { Modifier(Keys.ModCtrl, "Ctrl", 1.75f) };
        Letters(home, new int[] { 'a', 's', 'd', 'f', 'g', 'h', 'j', 'k', 'l' });
        home.Add(Char(51));
        home.Add(Char(52));
        home.Add(Named(Keys.Return, "Enter", 2.25f));
        Rows.Add(home);
        var lower = new List<OskKey> { Modifier(Keys.ModShift, "Shift", 2.25f) };
        Letters(lower, new int[] { 'z', 'x', 'c', 'v', 'b', 'n', 'm' });
        lower.Add(Char(54));
        lower.Add(Char(55));
        lower.Add(Char(56));
        lower.Add(Modifier(Keys.ModShift, "Shift", 2.75f));
        Rows.Add(lower);
        Rows.Add(new List<OskKey> { Modifier(Keys.ModCtrl, "Ctrl", 1.5f), Modifier(Keys.ModAlt, "Alt", 1.5f), Named(Keys.Space, "", 9),
            Modifier(Keys.ModAlt, "Alt", 1.5f), Modifier(Keys.ModCtrl, "Ctrl", 1.5f) });
    }

    static OskKey Char(int key)
    {
        return new OskKey { Key = key };
    }

    static OskKey Named(int key, string name, float w = 1)
    {
        return new OskKey { Key = key, Name = name, W = w };
    }

    static OskKey Modifier(int mod, string name, float w)
    {
        return new OskKey { Mod = mod, Name = name, W = w };
    }

    static void Letters(List<OskKey> row, int[] letters)
    {
        foreach (var c in letters)
            row.Add(Char(Keys.A + c - 'a'));
    }

    // Label shown on the key (for character keys, the character typed with the current Shift)
    public string Label(OskKey key)
    {
        if (key.Name != "" || key.Mod != 0 || key.Key == Keys.Space)
            return key.Name;
        var c = Keys.UsChar(key.Key, (Mods & Keys.ModShift) != 0);
        return c != 0 ? ((char)c).ToString() : "";
    }

    // Pointer pointer (left hand 0, right hand or mouse 1) pointed at a position within the picture (top left 0,0, bottom right 1,1). Highlights that key (nothing if there is no key there)
    public void Point(int pointer, float u, float v)
    {
        HoverRow[pointer] = -1;
        HoverAt[pointer] = -1;
        if (u < 0 || u >= 1 || v < 0 || v >= 1)
            return;
        var r = (int)(v * Rows.Count);
        var x = u * Units;
        var left = 0.0f;
        for (var i = 0; i < Rows[r].Count; i++)
        {
            if (x >= left && x < left + Rows[r][i].W)
            {
                HoverRow[pointer] = r;
                HoverAt[pointer] = i;
                return;
            }
            left += Rows[r][i].W;
        }
    }

    // The key pointer is pointing at (null if none)
    public OskKey? Hovered(int pointer)
    {
        return HoverRow[pointer] >= 0 ? Rows[HoverRow[pointer]][HoverAt[pointer]] : null;
    }

    // Whether any pointer is pointing at the i-th key of row r
    public bool Lit(int r, int i)
    {
        return (HoverRow[0] == r && HoverAt[0] == i) || (HoverRow[1] == r && HoverAt[1] == i);
    }

    // Presses a key. A modifier key is only applied to (or released from) the next keystroke; other keys return the sequence of press and release events and release the modifiers
    public List<EventData> Press(OskKey key)
    {
        var events = new List<EventData>();
        if (key.Mod != 0)
        {
            Mods ^= key.Mod;
            return events;
        }
        // Keys pressed with Ctrl or Alt do not produce characters
        var plain = Keys.UsChar(key.Key, false);
        var typed = (Mods & (Keys.ModCtrl | Keys.ModAlt)) == 0 ? Keys.UsChar(key.Key, (Mods & Keys.ModShift) != 0) : 0;
        events.Add(new EventData { Kind = EventKind.KeyDown, Key = key.Key, Mods = Mods, Typed = typed, Plain = plain });
        events.Add(new EventData { Kind = EventKind.KeyUp, Key = key.Key, Mods = 0, Typed = 0, Plain = 0 });
        Mods = 0;
        return events;
    }

    // Lider draws the picture in front of the other windows (it is not kept as a picture placed on the display)
    public TextureRef? Texture()
    {
        return null;
    }
}
