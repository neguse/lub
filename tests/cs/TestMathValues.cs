using System;
using System.Collections.Generic;
using Lub;

// Vec と Mat4 は値で、代入と引数渡しは独立した copy になる。
public static class TestMathValues
{
    static void Bump(Vec3 v)
    {
        v.X = 100;
    }

    static void SetCorner(Mat4 m)
    {
        m.M33 = 100;
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu", Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        var a = new Vec3(1, 2, 3);
        var b = a;
        b.X = 9;
        Check.That(a.X == 1, "a copy must not write back");

        Bump(a);
        Check.That(a.X == 1, "an argument must be a copy");

        var list = new List<Vec2> { new Vec2(1, 2) };
        var e = list[0];
        e.X = 9;
        Check.That(list[0].X == 1, "a list element read must be a copy");

        var m = Mat4.Identity();
        var n = m;
        n.M00 = 9;
        Check.That(m.M00 == 1, "a Mat4 copy must not write back");
        SetCorner(m);
        Check.That(m.M33 == 1, "a Mat4 argument must be a copy");

        Check.That(new Mat4().M00 == 0, "new Mat4() is zero");
        Check.That(Mat4.Zero().M33 == 0, "Zero is zero");

        var t = Mat4.Translate(new Vec3(1, 2, 3));
        var values = t.ToList();
        Check.That(values.Count == 16, "ToList has 16 values");
        Check.That(values[3] == 1 && values[7] == 2 && values[11] == 3, "ToList is row-major");
        var round = Mat4.FromList(values, 0);
        Check.That(round.M03 == 1 && round.M13 == 2 && round.M23 == 3 && round.M33 == 1, "FromList reads row-major");

        var inv = t.Inverse() * t;
        Check.That(inv.M00 == 1 && inv.M03 == 0, "Inverse * m is identity");
        var tr = t.Transpose();
        Check.That(tr.M30 == 1 && tr.M31 == 2 && tr.M32 == 3, "Transpose swaps rows and columns");

        Vec3? none = null;
        Check.That((none ?? new Vec3(7, 0, 0)).X == 7, "Vec3? holds null");

        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        return Check.OnError(message);
    }
}
