// host の関数の登録が module の struct と合わないときの読み込みの拒否 (host_test.c の reject)
using Eng;

public static class Reject
{
    public static float Run()
    {
        var a = new Vec2 { X = 1, Y = 2 };
        var n = new Named { Name = "n", W = 2 };
        Phys.Pick(1, out Hit h);
        return Phys.Dot(a, a) + Phys.Weight(n) + h.T;
    }
}
