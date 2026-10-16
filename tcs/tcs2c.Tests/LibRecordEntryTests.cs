namespace TinyCs.Tcs2c.Tests;

// --lib の entry は --ref の外部データ class を引数に取れる。
// host は tcs_new_<型>() で作った record をすぐ entry に渡し、game が保持した record は境界で昇格して残る。
// user の class を引数に取る method は entry にしない。
public class LibRecordEntryTests
{
    [CFact]
    public void LibMode_EntryTakesHostBuiltExternalRecord_AndKeptRecordSurvivesBoundaries()
    {
        const string stub = """
            namespace H { public class Ev { public int Kind; public float X; } }
            """;
        const string source = """
            using System.Collections.Generic;
            public static class G
            {
                static List<H.Ev> kept = new List<H.Ev>();
                public static void OnEv(H.Ev e) { if (e.Kind > 0) kept.Add(e); }
                public static void OnMine(Mine m) { }
                public static void Report()
                {
                    foreach (var e in kept) System.Console.WriteLine(e.Kind + ":" + e.X);
                }
            }
            public class Mine { public int V; }
            """;
        var exported = IlExport.Export([source], specializeGenerics: true,
            referenceSources: [stub]);
        Assert.Empty(exported.Diagnostics);
        var c = new CEmitter(exported, digestF32: false).Emit(null, lib: true);
        Assert.Contains("tcs_entry_G_OnEv(Tcs_H_Ev * v_e_0)", c);
        Assert.DoesNotContain("tcs_entry_G_OnMine", c);
        // 境界ごとに nursery を garbage で埋め、昇格しなかった record が壊れる形にする
        const string host = """

            int main(void)
            {
                int i, j;
                tcs_lib_init();
                for (i = 0; i < 4; i++) {
                    Tcs_H_Ev *e = tcs_new_H_Ev();
                    e->f_kind = i;
                    e->f_x = 0.5f * (float)i;
                    tcs_entry_G_OnEv(e);
                    for (j = 0; j < 64; j++) tcs_new_H_Ev()->f_kind = -1;
                    tcs_lib_gc();
                }
                tcs_entry_G_Report();
                return 0;
            }
            """;
        const string expected = "1:0.5\n2:1\n3:1.5";
        Assert.Equal(expected, Backends.CompileAndRunC(c + host));
        Assert.Equal(expected, Backends.CompileAndRunC(c + host, stress: true));
    }
}
