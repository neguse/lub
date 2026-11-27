namespace TinyCs.Tcs2c.Tests;

// --lib entries can take strings as arguments and return a number.
// The host passes a string created with tcs_string_new() straight to an entry, and strings that the program keeps are promoted at the boundary and survive.
// Methods that return an object or have out parameters are not made into entries.
public class LibStringEntryTests
{
    [CFact]
    public void LibMode_EntryTakesHostBuiltString_ReturnsNumber_AndKeptStringSurvivesBoundaries()
    {
        const string source = """
            using System.Collections.Generic;
            public static class G
            {
                static List<string> kept = new List<string>();
                static string joined = "";
                public static int Take(string s, int scale)
                {
                    // Garbage while s is alive
                    var junk = new List<string>();
                    for (int i = 0; i < 200; i++) junk.Add("x" + i);
                    kept.Add(s);
                    joined = joined + s + "|";
                    return s.RuneCount() * scale + junk.Count;
                }
                public static bool Has(string? s) { return s != null && kept.Contains(s); }
                public static float Half(int n) { return n * 0.5f; }
                public static string Name() { return joined; }
                public static void Out(out int n) { n = 1; }
                public static void Report()
                {
                    System.Console.WriteLine(joined + " " + kept.Count + " " + kept[0]);
                }
            }
            """;
        var c = SelfHostC.Emit([source], null, lib: true);
        Assert.Contains("int32_t\ntcs_entry_G_Take(TcsString * v_s_0, int32_t v_scale_1)", c);
        Assert.Contains("bool\ntcs_entry_G_Has(TcsString * v_s_0)", c);
        Assert.Contains("float\ntcs_entry_G_Half(int32_t v_n_0)", c);
        Assert.DoesNotContain("tcs_entry_G_Name", c);
        Assert.DoesNotContain("tcs_entry_G_Out", c);
        // Fills the nursery with garbage at every boundary so that strings that were not promoted get corrupted
        const string host = """

            static TcsString *host_text(const char *s) { return tcs_string_new((const unsigned char *)s, strlen(s)); }

            int main(void)
            {
                int i, j, sum = 0;
                char buf[32];
                tcs_lib_init();
                for (i = 0; i < 50; i++) {
                    snprintf(buf, sizeof buf, "a%d", i);
                    sum += tcs_entry_G_Take(host_text(buf), 2);
                    for (j = 0; j < 64; j++) host_text("garbage");
                    tcs_lib_gc();
                }
                printf("%d %d %d %d %.1f\n", sum, tcs_entry_G_Has(host_text("a7")), tcs_entry_G_Has(host_text("b")),
                    tcs_entry_G_Has(NULL), (double)tcs_entry_G_Half(3));
                fflush(stdout);
                tcs_entry_G_Report();
                return 0;
            }
            """;
        var joined = string.Concat(Enumerable.Range(0, 50).Select(i => "a" + i + "|"));
        // 10 one-digit names (length 2) and 40 two-digit names (length 3), times 2, plus 200 per call
        var expected = (10 * 2 * 2 + 40 * 3 * 2 + 50 * 200) + " 1 0 0 1.5\n" + joined + " 50 a0";
        Assert.Equal(expected, Backends.CompileAndRunC(c + host));
        Assert.Equal(expected, Backends.CompileAndRunC(c + host, stress: true));
    }
}
