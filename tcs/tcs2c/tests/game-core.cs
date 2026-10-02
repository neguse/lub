using System;

public class Point
{
    public float X, Y;
    public Point(float x = 2, float y = 3) { X = x; Y = y; }
    public float Shift(float amount = 4) { X += amount; return X; }
}

public class GameCore
{
    static int order;
    static int Next() { order++; return order; }
    static float[] Make() { return new float[] { Next(), Next(), 3.5f }; }

    public static void Main()
    {
        var values = Make();
        Console.WriteLine(values.Length);
        Console.WriteLine(values[0]);
        Console.WriteLine(values[1]);
        Console.WriteLine(values[2]);
        var nested = new int[][] { new int[] { 7 }, new int[] { 8, 9 } };
        Console.WriteLine(nested[1][1]);
        var empty = new float[] {};
        Console.WriteLine(empty.Length);
        var point = new Point();
        Console.WriteLine(point.Shift());
        Console.WriteLine(point.Y);
        Console.WriteLine((float)Math.Sqrt(9));
        Console.WriteLine((float)Math.Sin(0));
        Console.WriteLine((float)Math.Cos(0));
        Console.WriteLine((float)Math.Atan2(0, 1));
        Console.WriteLine((float)Math.Pow(2, 3));
        Console.WriteLine((int)Math.Floor(-1.25f));
        Console.WriteLine(Math.Min(Next(), Next()));
        Console.WriteLine(Math.Abs(-5));
        Console.WriteLine(1f / 2f);
        Console.WriteLine(3f / 2);
        Console.WriteLine(3 / 2);
        Console.WriteLine("0123456789".IndexOf("987".Substring(1, 1)));
        Console.WriteLine("abcabc".IndexOf("bc", 2));
        Console.WriteLine("abc".IndexOf("d"));
        Console.WriteLine("abc".IndexOf("", 3));
        Console.WriteLine("abc".Substring(1));
        Console.WriteLine("abc".Substring(3, 0));
        Console.WriteLine("a\0b".IndexOf("\0b"));
        float promoted = 0;
        promoted = 1.5f;
        Console.WriteLine(promoted);
        Console.WriteLine(-2147483648);
        Console.WriteLine(4294967295f);
        Console.WriteLine(new ShiftPoint().Read());
    }
}

public class ShiftPoint : Point
{
    public ShiftPoint() : base() {}
    public float Read() { return base.Shift(); }
}
