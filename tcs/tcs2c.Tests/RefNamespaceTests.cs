namespace TinyCs.Tcs2c.Tests;

// --ref の別 namespace にある同じ simple 名の型 (A.Opts と B.Opts) は別の外部
// class として出る
public class RefNamespaceTests
{
    [CFact]
    public void RefTypesWithSameSimpleNameInDifferentNamespaces_AreBothExported()
    {
        const string stub = """
            namespace A { public class Opts { public int X; public int Y; } public static class Fa { public static int Use(Opts o) { return 0; } } }
            namespace B { public class Opts { public float Scale; public float Rot; } public static class Fb { public static float Use(Opts o) { return 0; } } }
            """;
        const string source = """
            public static class G
            {
                public static void Main()
                {
                    var a = new A.Opts(); a.X = 3; a.Y = 4;
                    var b = new B.Opts(); b.Scale = 1.5f; b.Rot = 2f;
                    System.Console.WriteLine(A.Fa.Use(a) + B.Fb.Use(b));
                }
            }
            """;
        var exported = IlExport.Export([source], specializeGenerics: true,
            referenceSources: [stub]);
        Assert.Empty(exported.Diagnostics);
        Assert.Equal(["A_Opts", "B_Opts"], exported.Classes.Where(c => c.IsExternal)
            .Select(c => c.Name).OrderBy(n => n, StringComparer.Ordinal));
        var c = new CEmitter(exported, digestF32: false).Emit("G");
        // host は生成 C の struct を同じ translation unit で読む
        const string host = """

            int32_t tcs_host_a_fa_use(Tcs_A_Opts *o) { return o->f_x * 10 + o->f_y; }
            float tcs_host_b_fb_use(Tcs_B_Opts *o) { return o->f_scale * o->f_rot; }
            """;
        Assert.Equal("37", Backends.CompileAndRunC(c + host));
    }
}
