// 画面のキーボード: 並んだ key を押すと、つないだキーボードと同じ key の event を作る。
// Shift、Ctrl、Alt は、押すと次の 1 打に効く (もう一度押すと外れる)。
// 並びは US 配列で、どの行も幅は Units。
using System.Collections.Generic;
using Lub;

namespace LiderCore;

public class OskKey
{
    // SDL の scancode (修飾 key は 0)
    public int Key;
    // 修飾 key なら、その bit (Keys.ModShift など)
    public int Mod;
    // 文字でない key の字 (文字の key は空で、US 配列の字を出す)
    public string Name = "";
    // 幅 (文字の key 1 つが 1)
    public float W = 1;
}

public class Osk : IContent
{
    public const int Units = 15;
    public readonly List<List<OskKey>> Rows = new List<List<OskKey>>();
    public bool Shown;
    // 次の 1 打に効く修飾 key (Keys.Mod* の bit の和)
    public int Mods;
    // 指している key (行と、行の中の番号。指していなければ -1)
    public int HoverRow = -1;
    public int HoverAt = -1;
    // 描いた絵 (OskView が描く)
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

    // key に出す字 (文字の key は、今の Shift で打つ字)
    public string Label(OskKey key)
    {
        if (key.Name != "" || key.Mod != 0 || key.Key == Keys.Space)
            return key.Name;
        var c = Keys.UsChar(key.Key, (Mods & Keys.ModShift) != 0);
        return c != 0 ? ((char)c).ToString() : "";
    }

    // 絵の中の位置 (左上が 0,0、右下が 1,1) にある key の、行と行の中の番号を HoverRow と HoverAt に入れる (無ければ -1)
    public void Point(float u, float v)
    {
        HoverRow = -1;
        HoverAt = -1;
        if (u < 0 || u >= 1 || v < 0 || v >= 1)
            return;
        var r = (int)(v * Rows.Count);
        var x = u * Units;
        var left = 0.0f;
        for (var i = 0; i < Rows[r].Count; i++)
        {
            if (x >= left && x < left + Rows[r][i].W)
            {
                HoverRow = r;
                HoverAt = i;
                return;
            }
            left += Rows[r][i].W;
        }
    }

    // 指している key (指していなければ null)
    public OskKey? Hovered => HoverRow >= 0 ? Rows[HoverRow][HoverAt] : null;

    // key を押す。修飾 key なら次の 1 打に効かせる (か外す) だけで、ほかの key は、押して離す event の並びを返し、修飾 key を外す
    public List<EventData> Press(OskKey key)
    {
        var events = new List<EventData>();
        if (key.Mod != 0)
        {
            Mods ^= key.Mod;
            return events;
        }
        // Ctrl か Alt と一緒の key は、文字にしない
        var plain = Keys.UsChar(key.Key, false);
        var typed = (Mods & (Keys.ModCtrl | Keys.ModAlt)) == 0 ? Keys.UsChar(key.Key, (Mods & Keys.ModShift) != 0) : 0;
        events.Add(new EventData { Kind = EventKind.KeyDown, Key = key.Key, Mods = Mods, Typed = typed, Plain = plain });
        events.Add(new EventData { Kind = EventKind.KeyUp, Key = key.Key, Mods = 0, Typed = 0, Plain = 0 });
        Mods = 0;
        return events;
    }

    // 絵は、ほかのウィンドウより手前に Lider が描く (画面に貼る絵としては持たない)
    public TextureRef? Texture()
    {
        return null;
    }
}
