// Rejection of loading when a host function registration does not match the module's structs (reject in host_test.c)
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
