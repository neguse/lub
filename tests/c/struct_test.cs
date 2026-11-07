using System;
using Lub;

class StructGame
{
    public static void Main()
    {
        var flag = new TestFlag { On = true, Op = Gfx.BlendOp.Max };
        var hit = StructTest.Make(flag with { On = false },
            new TestOpts { Flag = flag, Hit = new TestHit { Id = 3, T = 0.5f, Near = true, At = new TestPoint { X = 7, Y = 8 } } });
        Console.WriteLine(hit.Id + " " + hit.T + " " + hit.Near + " " + hit.At.X + " " + hit.At.Y);
        // 見つからない (LUB_NOT_FOUND) struct の戻り値は 0 の値
        var none = StructTest.Make(flag, null);
        Console.WriteLine(none.Id + " " + none.Near);
        bool found = StructTest.Find(4, out var h, out var f);
        Console.WriteLine(found + " " + h.Id + " " + h.Near + " " + h.At.Y + " " + f.On + " " + (int)f.Op);
        bool missing = StructTest.Find(-1, out var h2, out var f2);
        Console.WriteLine(missing + " " + h2.Id + " " + f2.On);
        var flipped = StructTest.Flip(flag, new TestPair());
        Console.WriteLine(flipped.On + " " + (int)flipped.Op);
    }
}
