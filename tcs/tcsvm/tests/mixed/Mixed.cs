// mixed-mode の最小の試験 (doc/object-model.md M-4): 同じ source を VM と tcs2c (lib) の両方にし、VM が作った object を
// tcs2c の生成 C の関数が読み書きする。class の payload ({ type_id; field... }) の形が両方で同じことを確かめる。
public class Pt
{
    public int X;
    public float Y;
    public bool On;
    public Pt Next;
    public int Z;
}

public static class Game
{
    static Pt keep;

    public static void Setup()
    {
        keep = new Pt { X = 7, Y = 2.5f, On = true, Z = -3 };
        keep.Next = keep;
    }

    public static Pt Get() => keep;
    public static int Sum(Pt p) => p.X + p.Z;
    public static void Bump(Pt p) { p.X += 10; p.Y *= 2; }
    public static float Y(Pt p) => p.Y;
    public static void Frame() { }
    public static void Report() { }
}
