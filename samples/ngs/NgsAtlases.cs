using System.Collections.Generic;

/// <summary>atlas 内の sprite rect。原典 NSP の rect 順を保つ (gameplay が番号で
/// 引く)。Rect は cs-lib の class なので static 初期化子では作れず、Init で
/// 埋める (生成 Lua は sample → cs-lib の順に定義される)。</summary>
public static class NgsAtlases
{
    public static List<Rect> Cursor = new List<Rect>();
    public static List<Rect> Jiki = new List<Rect>();
    public static List<Rect> Enemy = new List<Rect>();

    static bool ready = false;

    static void Add(List<Rect> l, int x, int y, int w, int h)
    {
        l.Add(new Rect(x, y, w, h));
    }

    public static void Init()
    {
        if (ready) return;
        ready = true;
        Add(Cursor, 0, 0, 8, 8);
        Add(Cursor, 8, 0, 8, 8);
        Add(Cursor, 16, 0, 8, 8);
        Add(Cursor, 24, 0, 8, 8);

        Add(Jiki, 0, 0, 16, 16);
        Add(Jiki, 16, 0, 16, 16);
        Add(Jiki, 32, 0, 16, 16);
        Add(Jiki, 48, 0, 16, 16);
        Add(Jiki, 64, 0, 16, 16);
        Add(Jiki, 0, 16, 16, 16);
        Add(Jiki, 16, 16, 16, 16);
        Add(Jiki, 32, 16, 16, 16);
        Add(Jiki, 48, 16, 16, 16);
        Add(Jiki, 64, 16, 16, 8);
        Add(Jiki, 64, 24, 10, 5);
        Add(Jiki, 64, 29, 6, 3);
        Add(Jiki, 50, 18, 12, 14);

        Add(Enemy, 0, 0, 16, 16);
        Add(Enemy, 16, 0, 16, 16);
        Add(Enemy, 32, 0, 16, 16);
        Add(Enemy, 48, 0, 16, 16);
        Add(Enemy, 0, 16, 16, 16);
        Add(Enemy, 16, 16, 6, 7);
        Add(Enemy, 22, 16, 6, 7);
        Add(Enemy, 29, 16, 2, 16);
        Add(Enemy, 30, 16, 2, 16);
        Add(Enemy, 0, 32, 26, 32);
        Add(Enemy, 27, 32, 26, 32);
        Add(Enemy, 64, 0, 26, 32);
        Add(Enemy, 64, 32, 26, 32);
        Add(Enemy, 51, 21, 3, 3);
        Add(Enemy, 51, 25, 3, 3);
        Add(Enemy, 50, 16, 5, 4);
        Add(Enemy, 55, 16, 1, 32);
        Add(Enemy, 90, 0, 26, 32);
        Add(Enemy, 32, 16, 6, 6);
        Add(Enemy, 40, 16, 8, 8);
        Add(Enemy, 32, 24, 8, 8);
        Add(Enemy, 40, 24, 8, 8);
        Add(Enemy, 90, 32, 26, 32);
    }
}
