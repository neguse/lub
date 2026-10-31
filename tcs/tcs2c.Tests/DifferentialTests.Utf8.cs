using System.Text;

namespace TinyCs.Tcs2c.Tests;

// TinySystem.Text (RuneCount / RuneSubstring / Utf8Bytes) が tcs2c (runtime/utf8)、Lua (tcs_utf8 module)、.NET (TinySystem/Utf8.cs) で同じ結果を返す
public partial class DifferentialTests
{
    private static string Utf8Program(string body) => $$"""
        using System;
        using System.Collections.Generic;
        using TinySystem.Text;
        public class Holder { public Utf8Bytes Bytes; public Utf8Bytes? Maybe; public static Utf8Bytes Shared; }
        public struct Wrap { public Utf8Bytes U; public int N; }
        public class Box<T> { public T Value; }
        public class P
        {
            public static void Main()
            {
                {{body}}
            }
        }
        """;

    private const string Utf8Corpus =
        """var corpus = new[] { "", "abc", "é", "日本語", "\U0001F600", "a\U0001F600béc", "éé日\U0001F600\U0001F600" };""";

    [CFact]
    public void Utf8_RuneCountAndSubstring_AgreeOnEveryRange()
    {
        var output = Backends.AssertAllPathsAgree(Utf8Program(Utf8Corpus + """
            foreach (var s in corpus)
            {
                int n = s.RuneCount();
                Console.WriteLine(n);
                for (int start = 0; start <= n; start++)
                    for (int count = 0; count <= n - start; count++)
                        Console.WriteLine(start + "," + count + "=" + s.RuneSubstring(start, count) + "|" + Runes.RuneCount(Runes.RuneSubstring(s, start, count)));
            }
            string none = null;
            Console.WriteLine(none?.RuneCount() ?? -1);
            Console.WriteLine(none?.RuneSubstring(0, 0) ?? "null");
            """), "P");
        Assert.Contains("1,1=\U0001F600|1", output);
    }

    [CFact]
    public void Utf8Bytes_LengthIndexSliceToString_AgreeOnEveryRange()
    {
        var output = Backends.AssertAllPathsAgree(Utf8Program(Utf8Corpus + """
            foreach (var s in corpus)
            {
                var u = new Utf8Bytes(s);
                string bytes = "";
                for (int i = 0; i < u.Length; i++) bytes += u[i] + " ";
                Console.WriteLine(u.Length + ":" + bytes + ":" + u.ToString());
                for (int start = 0; start <= u.Length; start++)
                    for (int count = 0; count <= u.Length - start; count++)
                    {
                        // 文字の途中で切った slice の ToString は、壊れた並びを U+FFFD にする
                        var slice = u.Slice(start, count);
                        string first = count == 0 ? "-" : "" + slice[0];
                        Console.WriteLine(start + "," + count + "=" + slice.Length + "," + first + "," + slice.ToString() + "," + slice.Slice(0, count).ToString().RuneCount());
                    }
            }
            """), "P");
        Assert.Contains("240 159 152 128", output);
        Assert.Contains("�", output);
    }

    [CFact]
    public void Utf8Bytes_FlowsThroughLocalsFieldsListsAndStructs()
    {
        var output = Backends.AssertAllPathsAgree(Utf8Program("""
            var h = new Holder();
            h.Bytes = new Utf8Bytes("日本");
            int first = h.Bytes[0];
            byte second = h.Bytes[1];
            var third = h.Bytes[2];
            Console.WriteLine(first + second + third);
            var list = new List<Utf8Bytes> { h.Bytes, h.Bytes.Slice(3, 3) };
            Console.WriteLine(list[1].ToString() + list[1].Length);
            var w = new Wrap { U = list[1], N = 1 };
            var copy = w;
            copy.U = copy.U.Slice(0, 0);
            Console.WriteLine(w.U.Length + ":" + copy.U.Length);
            var array = new Utf8Bytes[] { list[1] };
            Console.WriteLine(array[0].ToString());
            var d = new Dictionary<string, Utf8Bytes>();
            d["k"] = list[1];
            Console.WriteLine(d["k"].Length);
            foreach (var u in list) Console.WriteLine(u.Length);
            """), "P");
        Assert.Equal("""
            546
            本3
            3:0
            本
            3
            6
            3
            """.ReplaceLineEndings("\n"), output);
    }

    [CFact]
    public void Utf8Bytes_DefaultIsAnEmptyViewEverywhere()
    {
        var output = Backends.AssertAllPathsAgree(Utf8Program("""
            Utf8Bytes local = default;
            var d = new Dictionary<string, Utf8Bytes>();
            d.TryGetValue("none", out var missing);
            var views = new List<Utf8Bytes>
            {
                default(Utf8Bytes), new Utf8Bytes(), local, new Holder().Bytes, Holder.Shared,
                (new Utf8Bytes[2])[1], new Wrap().U, new Box<Utf8Bytes>().Value, missing,
                new Utf8Bytes(""),
            };
            foreach (var u in views)
                Console.WriteLine(u.Length + "[" + u.ToString() + "]" + u.Slice(0, 0).Length + "[" + u.Slice(0, 0).ToString() + "]");
            """), "P");
        Assert.Equal(string.Join("\n", Enumerable.Repeat("0[]0[]", 10)), output);
    }

    [CTheory]
    [InlineData("default(Utf8Bytes)[0]")]
    [InlineData("new Holder().Bytes[0]")]
    [InlineData("new Wrap().U[-1]")]
    [InlineData("(new Utf8Bytes[1])[0].Slice(0, 1).Length")]
    [InlineData("new Box<Utf8Bytes>().Value.Slice(1, 0).Length")]
    public void Utf8Bytes_DefaultIsOutOfRange(string call)
    {
        Assert.Equal("before", Backends.AssertFaultOnAllPaths(Utf8Program($$"""
            Console.WriteLine("before");
            Console.WriteLine({{call}});
            """), "P"));
    }

    [CFact]
    public void Utf8Bytes_NullableFollowsStructNullable()
    {
        var output = Backends.AssertAllPathsAgree(Utf8Program("""
            var h = new Holder();
            Utf8Bytes? none = h.Maybe;
            Console.WriteLine((none == null) + ":" + none.HasValue);
            Console.WriteLine(none?.Length ?? -1);
            Console.WriteLine(none?[0] ?? -1);
            Console.WriteLine(none?.ToString() ?? "null");
            Console.WriteLine(none?.Slice(0, 0).Length ?? -1);
            h.Maybe = new Utf8Bytes("ab");
            Utf8Bytes? some = h.Maybe;
            Console.WriteLine((some == null) + ":" + some.HasValue + ":" + some.Value.Length);
            Console.WriteLine(some?.Length ?? -1);
            Console.WriteLine(some?[1] ?? -1);
            Console.WriteLine(some?.ToString() ?? "null");
            Console.WriteLine(some?.Slice(1, 1).ToString() ?? "null");
            var d = none ?? new Utf8Bytes("x");
            Console.WriteLine(d.ToString());
            """), "P");
        Assert.Equal("""
            True:False
            -1
            -1
            null
            -1
            False:True:2
            2
            98
            ab
            b
            x
            """.ReplaceLineEndings("\n"), output);
    }

    [CTheory]
    [InlineData("""new Utf8Bytes("abc")[3]""")]
    [InlineData("""new Utf8Bytes("abc")[-1]""")]
    [InlineData("""new Utf8Bytes("abc").Slice(1, 1)[1]""")]
    [InlineData("""new Utf8Bytes("abc").Slice(-1, 1).Length""")]
    [InlineData("""new Utf8Bytes("abc").Slice(4, 0).Length""")]
    [InlineData("""new Utf8Bytes("abc").Slice(1, 3).Length""")]
    [InlineData("""new Utf8Bytes("abc").Slice(1, -1).Length""")]
    [InlineData("""new Utf8Bytes(Nothing()).Length""")]
    [InlineData(""" "é".RuneSubstring(2, 0)""")]
    [InlineData(""" "é".RuneSubstring(0, 2)""")]
    [InlineData(""" "é".RuneSubstring(-1, 1)""")]
    [InlineData(""" "é".RuneSubstring(0, -1)""")]
    [InlineData("Nothing().RuneCount()")]
    [InlineData("Nothing().RuneSubstring(0, 0)")]
    public void Utf8_OutOfRangeAndNull_FaultAfterPrecedingOutput(string call)
    {
        Assert.Equal("before", Backends.AssertFaultOnAllPaths($$"""
            using System;
            using TinySystem.Text;
            public class P
            {
                static string Nothing() { return null; }
                public static void Main()
                {
                    Console.WriteLine("before");
                    Console.WriteLine({{call}});
                }
            }
            """, "P"));
    }

    [Theory]
    [InlineData("Func<string, int> f = Runes.RuneCount;", "TCS1001")]
    [InlineData("Func<int> f = \"\".RuneSubstring(0, 0).RuneCount;", "TCS1001")]
    [InlineData("Func<int, int, Utf8Bytes> f = new Utf8Bytes(\"\").Slice;", "TCS1001")]
    [InlineData("Console.WriteLine(\"\" + new Utf8Bytes(\"\"));", "TCS1001")]
    [InlineData("Console.WriteLine($\"{new Utf8Bytes(\"\")}\");", "TCS1001")]
    [InlineData("Console.WriteLine(new Utf8Bytes(\"\"));", "TCS1001")]
    [InlineData("object o = new Utf8Bytes(\"\");", "StructBoxing")]
    [InlineData("Utf8Bytes? n = null; object o = n;", "StructBoxing")]
    [InlineData("var o = (object)new Utf8Bytes(\"\");", "StructBoxing")]
    [InlineData("var d = new Dictionary<Utf8Bytes, int>();", "TCS1001")]
    [InlineData("var u = new Utf8Bytes(\"\"); Console.WriteLine(u == null);", "CS0019")]
    [InlineData("var u = new Utf8Bytes(\"\"); var v = u; Console.WriteLine(u == v);", "CS0019")]
    [InlineData("var u = new Utf8Bytes(\"\"); Console.WriteLine(u?.Length);", "CS0023")]
    [InlineData("var u = new Utf8Bytes(\"\"); Console.WriteLine(u.Equals(u));", "TCS1002")]
    [InlineData("var l = new List<Utf8Bytes>(); Console.WriteLine(l.Contains(default));", "TCS1002")]
    [InlineData("var l = new List<Utf8Bytes>(); Console.WriteLine(l.IndexOf(default));", "TCS1002")]
    [InlineData("var l = new List<Utf8Bytes>(); l.Remove(default);", "TCS1002")]
    public void Utf8_UnsupportedForms_AreDiagnosed(string statement, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Backends.EmitC([Utf8Program(statement)], "P"));
        Assert.Contains(expected, ex.Message);
        ex = Assert.Throws<InvalidOperationException>(() => Backends.RunLua([Utf8Program(statement)], "P"));
        Assert.Contains(expected, ex.Message);
    }

    // Trim と引数なしの Split の空白は .NET の char.IsWhiteSpace と同じ。空白は全部 U+3000 以下
    [CFact]
    public void TrimAndSplit_WhiteSpaceMatchesDotnetForEveryRune()
    {
        var output = Backends.AssertAllPathsAgree("""
            using System;
            public class P
            {
                public static void Main()
                {
                    string trimmed = "";
                    string split = "";
                    for (int rune = 0; rune <= 0x3100; rune++)
                    {
                        if (rune >= 0xD800 && rune <= 0xDFFF) continue;
                        string text = char.ConvertFromUtf32(rune);
                        if ((text + "x" + text).Trim() == "x") trimmed += rune + ",";
                        if (("x" + text + "y").Split().Length == 2) split += rune + ",";
                    }
                    Console.WriteLine(trimmed);
                    Console.WriteLine(split);
                    Console.WriteLine(char.ConvertFromUtf32(0x10FFFF).Trim() == char.ConvertFromUtf32(0x10FFFF));
                }
            }
            """, "P");
        Assert.Contains("133,160,5760,8192,", output);
        Assert.Contains("8239,8287,12288,", output);
    }

    [CFact]
    public void TrimAndSplit_MixedTextAgrees()
    {
        var output = Backends.AssertAllPathsAgree("""
            using System;
            public class P
            {
                static void Show(string s)
                {
                    Console.WriteLine("[" + s.Trim() + "]");
                    Console.WriteLine(string.Join("|", s.Split()));
                }
                public static void Main()
                {
                    Show("");
                    Show("  ");
                    Show("abc");
                    Show(" \t\n\r\f\v abc \t\n");
                    Show("\u3000\u00a0日本 語\u2003\u0085");
                    Show("a  b\u3000\u3000c");
                    Show("é\u00a0é\u2028😀\u2029");
                    Show("\u200b\ufeffx\ufeff\u200b");
                }
            }
            """, "P");
        Assert.Contains("[日本 語]", output);
        Assert.Contains("a||b||c", output);
    }

    [Theory]
    [InlineData("int x = 5; byte b = (byte)x;")]
    [InlineData("int x = 5; byte? b = (byte?)x;")]
    [InlineData("byte b = unchecked((byte)300);")]
    [InlineData("byte b = unchecked((byte)-1);")]
    [InlineData("byte b = (byte)1.5f;")]
    [InlineData("byte b = 1; b++;")]
    [InlineData("byte b = 1; b--;")]
    [InlineData("byte b = 1; ++b;")]
    [InlineData("byte b = 1; --b;")]
    [InlineData("byte b = 1; b += 1;")]
    [InlineData("byte b = 1; b -= 1;")]
    [InlineData("byte b = 1; b *= 2;")]
    [InlineData("byte b = 1; b <<= 1;")]
    [InlineData("byte? b = 1; b++;")]
    [InlineData("var f = new ByteField(); f.B++;")]
    [InlineData("var f = new ByteField(); f.B += 1;")]
    [InlineData("var a = new byte[1]; a[0]++;")]
    public void ByteNarrowing_BothBackendsStop(string statement)
    {
        var program = ByteProgram(statement);
        foreach (var run in new Func<string>[]
                 {
                     () => Backends.RunLua([program], "P"),
                     () => Backends.EmitC([program], "P"),
                 })
            Assert.Contains("error TCS1001: unsupported syntax: ByteNarrowing",
                Assert.Throws<InvalidOperationException>(run).Message);
    }

    [CTheory]
    [InlineData("record R(Utf8Bytes U);", "Utf8BytesInRecord")]
    [InlineData("record struct R(Utf8Bytes U);", "Utf8BytesInRecord")]
    [InlineData("record R { public Utf8Bytes? U; }", "Utf8BytesInRecord")]
    [InlineData("struct W { public Utf8Bytes U; } record R(W V);", "Utf8BytesInRecord")]
    public void Utf8Bytes_InRecord_BothBackendsStop(string declaration, string diagnostic)
    {
        var program = "using TinySystem.Text;\n" + declaration
            + "\nclass P { static void Main() { } }";
        foreach (var run in new Func<string>[]
                 {
                     () => Backends.RunLua([program], "P"),
                     () => Backends.EmitC([program], "P"),
                 })
            Assert.Contains($"error TCS1001: unsupported syntax: {diagnostic}",
                Assert.Throws<InvalidOperationException>(run).Message);
    }

    [CFact]
    public void Utf8Bytes_InStructInList_EqualitySearchStops()
    {
        var program = """
            using System.Collections.Generic;
            using TinySystem.Text;
            struct W { public Utf8Bytes U; }
            class P { static void Main() { var l = new List<W>(); l.Contains(default); } }
            """;
        Assert.Contains("error TCS1002",
            Assert.Throws<InvalidOperationException>(() => Backends.RunLua([program], "P")).Message);
    }

    [CFact]
    public void Byte_ReadingAndInRangeConstants_Agree()
    {
        var output = Backends.AssertAllPathsAgree(ByteProgram("""
            const int K = 7;
            byte a = 200;
            byte b = (byte)201;
            byte c = (byte)K;
            byte d = (byte)'a';
            byte e = new Utf8Bytes("é")[1];
            var f = new ByteField();
            f.B = e;
            e = new Utf8Bytes("é")[0];
            int sum = a + b;
            Console.WriteLine(a + ":" + b + ":" + c + ":" + d + ":" + e + ":" + f.B + ":" + sum);
            """), "P");
        Assert.Equal("200:201:7:97:195:169:401", output);
    }

    private static string ByteProgram(string body) => $$"""
        using System;
        using TinySystem.Text;
        public class ByteField { public byte B; }
        public class P
        {
            public static void Main()
            {
                {{body}}
            }
        }
        """;

    // 壊れた列の EnumerateRunes: program の中の string は (char) の 1 byte の連結でだけ壊れる (.NET では別の文字になる) ので、
    // 同じ byte 列の Encoding.UTF8 の結果を期待値にする
    [CTheory]
    [InlineData(0xF0, 0x9F)]
    [InlineData(0xF0, 0x9F, 0x98)]
    [InlineData(0x80)]
    [InlineData(0xBF, 0x80, 0x41)]
    [InlineData(0xC0, 0x80)]
    [InlineData(0xC1, 0x41)]
    [InlineData(0xE0, 0x80, 0x80)]
    [InlineData(0xE9, 0x41)]
    [InlineData(0xED, 0xA0, 0x80)]
    [InlineData(0xF0, 0x80, 0x80, 0x80)]
    [InlineData(0xF4, 0x90, 0x80, 0x80)]
    [InlineData(0xF5, 0x41)]
    [InlineData(0xFF)]
    [InlineData(0x41, 0xE6, 0x97, 0xA5, 0xE6, 0x97, 0x42)]
    [InlineData(0xEF, 0xBF, 0xBD)]
    public void EnumerateRunes_BrokenBytes_ReadsLikeEncodingUtf8(params int[] bytes)
    {
        var concat = string.Concat(bytes.Select(b => $" + (char)Id({b})"));
        var output = Backends.AssertParity($$"""
            using System;
            using TinySystem.Text;
            public class P
            {
                static int Id(int x) { return x; }
                public static void Main()
                {
                    string s = ""{{concat}};
                    string runes = "";
                    int count = 0;
                    foreach (var r in s.EnumerateRunes()) { runes += r.Value + ","; count++; }
                    Console.WriteLine(runes + count + ":" + s.RuneCount());
                }
            }
            """, "P");
        var text = Encoding.UTF8.GetString([.. bytes.Select(b => (byte)b)]);
        var expected = text.EnumerateRunes().Select(r => r.Value).ToList();
        Assert.Equal(string.Concat(expected.Select(v => v + ",")) + expected.Count + ":" + expected.Count, output);
    }

    // lib 形のフレーム境界 GC を越えて、Utf8Bytes が持つ文字列が生き残る
    [CFact]
    public void Utf8Bytes_SurvivesFrameBoundaryGc()
    {
        var output = Backends.AssertParityLib("""
            using System;
            using TinySystem.Text;
            public class Holder { public Utf8Bytes Bytes; }
            public class G
            {
                static Utf8Bytes kept;
                static Holder holder = new Holder();
                static System.Collections.Generic.List<Utf8Bytes> views = new System.Collections.Generic.List<Utf8Bytes>();
                static Utf8Bytes? maybe;
                static int frame;
                static int sum;
                public static void Setup()
                {
                    string text = "";
                    for (int i = 0; i < 40; i++) text += "日" + i;
                    kept = new Utf8Bytes(text).Slice(3, 30);
                    holder.Bytes = new Utf8Bytes(text + "h");
                    views.Add(new Utf8Bytes(text + "l"));
                    maybe = new Utf8Bytes(text + "m");
                }
                public static void Frame()
                {
                    frame++;
                    string garbage = "";
                    for (int i = 0; i < 40; i++) garbage += i;
                    kept = kept.Slice(1, kept.Length - 1);
                    sum += kept[0] + garbage.RuneCount();
                }
                public static void Report()
                {
                    Console.WriteLine(sum + ":" + kept.Length + ":" + kept.ToString().RuneCount());
                    Console.WriteLine(holder.Bytes.Length + ":" + views[0].Length + ":" + maybe?.Length);
                    Console.WriteLine(holder.Bytes[holder.Bytes.Length - 1] + ":" + views[0][views[0].Length - 1] + ":" + maybe?[maybe.Value.Length - 1]);
                }
            }
            """, "G", 5);
        Assert.StartsWith("1175:25:13\n", output);
    }

    // runtime/utf8 の復号が .NET の UTF8 と同じ位置で U+FFFD を出す。tcs の program からは壊れた byte 列を作れないので、C を直接比べる
    [CFact]
    public void Utf8_RuntimeScrubMatchesDotnetDecoderOnInvalidBytes()
    {
        var header = Path.Combine(Backends.ProjectRoot, "runtime", "utf8", "tcs_utf8.h").Replace('\\', '/');
        var impl = Path.Combine(Backends.ProjectRoot, "runtime", "utf8", "tcs_utf8.c").Replace('\\', '/');
        var driver = $$"""
            #include <stdio.h>
            #include <stdint.h>
            #include <stddef.h>
            #include "{{header}}"
            #include "{{impl}}"
            static uint32_t hash = 2166136261u;
            static void check(const unsigned char *s, size_t n)
            {
                unsigned char out[64];
                size_t length = tcs_utf8_scrub_length(s, n), i;
                tcs_utf8_scrub(s, n, out);
                for (i = 0; i < length; i++) hash = (hash ^ out[i]) * 16777619u;
                hash = (hash ^ (uint32_t)tcs_utf8_rune_count(s, n)) * 16777619u;
                for (i = 0; i < n;) {
                    uint32_t rune;
                    i += tcs_utf8_decode(s + i, n - i, &rune);
                    hash = (hash ^ rune) * 16777619u;
                }
            }
            int main(void)
            {
                static const unsigned char tail[] = { 0x00, 0x7F, 0x80, 0x8F, 0x90, 0x9F, 0xA0, 0xBF, 0xC0, 0xF5 };
                unsigned char s[4];
                int a, b, c, d;
                for (a = 0; a < 256; a++) {
                    s[0] = (unsigned char)a;
                    check(s, 1);
                    for (b = 0; b < 256; b++) {
                        s[1] = (unsigned char)b;
                        check(s, 2);
                        for (c = 0; c < 10; c++) {
                            s[2] = tail[c];
                            check(s, 3);
                            for (d = 0; d < 10; d++) { s[3] = tail[d]; check(s, 4); }
                        }
                    }
                }
                printf("%u\n", hash);
                return 0;
            }
            """;
        Assert.Equal(DotnetScrubHash(), Backends.CompileAndRunC(driver));
    }

    private static string DotnetScrubHash()
    {
        byte[] tail = [0x00, 0x7F, 0x80, 0x8F, 0x90, 0x9F, 0xA0, 0xBF, 0xC0, 0xF5];
        uint hash = 2166136261u;
        var s = new byte[4];
        void Check(int n)
        {
            var text = Encoding.UTF8.GetString(s, 0, n);
            foreach (var b in Encoding.UTF8.GetBytes(text)) hash = unchecked((hash ^ b) * 16777619u);
            hash = unchecked((hash ^ (uint)text.EnumerateRunes().Count()) * 16777619u);
            foreach (var rune in text.EnumerateRunes()) hash = unchecked((hash ^ (uint)rune.Value) * 16777619u);
        }
        for (var a = 0; a < 256; a++)
        {
            s[0] = (byte)a;
            Check(1);
            for (var b = 0; b < 256; b++)
            {
                s[1] = (byte)b;
                Check(2);
                foreach (var c in tail)
                {
                    s[2] = c;
                    Check(3);
                    foreach (var d in tail) { s[3] = d; Check(4); }
                }
            }
        }
        return hash.ToString();
    }
}
