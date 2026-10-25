namespace TinyCs.Tcs2c.Tests;

// string の順序は .NET の ordinal (UTF-16 の code unit の順) と同じ。
// UTF-8 の byte の順とは、U+10000 以上 (UTF-16 では surrogate D800-DBFF) と U+E000-U+FFFF の間だけ逆になる。
// 期待値は同じ program を InvariantGlobalization の CoreCLR で実行した stdout (TinySystem.props と同じ前提)。
// 表示は words の添字
public partial class DifferentialTests
{
    private const string StringOrderProgram = """
        using System;
        using System.Collections.Generic;
        using TinySystem.Linq;
        public class P
        {
            public static string Show(List<string> source, List<string> sorted)
            {
                string result = "";
                for (int i = 0; i < sorted.Count; i++) result += source.IndexOf(sorted[i]) + ",";
                return result;
            }
            public static void Main()
            {
                var issue = new List<string> { "b", "A", "a", "B" };
                issue.Sort();
                Console.WriteLine(issue[0] + "," + issue[1] + "," + issue[2] + "," + issue[3]);
                var words = new List<string>
                {
                    "b", "A", "a", "B", "", "ab", "abc", "a b", "a-b", "a_b", "!", "~", "0", "9", "Z", "z", "[", "{",
                    "e", "\u00E9", "f", "\u00C9", "\u00DF", "ss", "\u00C5",
                    "\u00AD", "\u00ADa", "a\u00AD",
                    "\u65E5", "\u65E5\u672C", "\u4E2D", "\u3042", "\u30A2",
                    "\uD7FF", "\uE000", "\uFF21", "\uFFFD", "\uFFFF",
                    "\U0001F600", "\U0001F600a", "\U0001F600b", "\U00020BB7", "\U0010FFFF", "a\U0001F600", "a\uE000",
                };
                var sorted = new List<string>(words);
                sorted.Sort();
                Console.WriteLine(Show(words, sorted));
                Console.WriteLine(Show(words, words.OrderBy(w => w)));
                Console.WriteLine(Show(words, words.OrderByDescending(w => w)));
                Console.WriteLine(words.IndexOf(words.Min()) + " " + words.IndexOf(words.Max()) + " " + words.IndexOf(words.Min(w => w)) + " " + words.IndexOf(words.Max(w => w)));
                string pairs = "";
                for (int i = 0; i + 1 < words.Count; i++)
                {
                    var pair = new List<string> { words[i], words[i + 1] };
                    pairs += words.IndexOf(pair.Min()) + "" + (pair.Max() == words[i] ? "<" : ">") + ",";
                }
                Console.WriteLine(pairs);
            }
        }
        """;

    private const string StringOrderExpected = """
        A,B,a,b
        4,10,12,13,1,3,14,16,2,7,8,9,5,6,27,43,44,0,18,20,23,15,17,11,25,26,24,21,22,19,31,32,30,28,29,33,38,39,40,41,42,34,35,36,37,
        4,10,12,13,1,3,14,16,2,7,8,9,5,6,27,43,44,0,18,20,23,15,17,11,25,26,24,21,22,19,31,32,30,28,29,33,38,39,40,41,42,34,35,36,37,
        37,36,35,34,42,41,40,39,38,33,29,28,30,32,31,19,22,21,24,26,25,11,17,15,23,20,18,0,44,43,27,6,5,9,8,7,2,16,14,3,1,13,12,10,4,
        4 37 4 37
        1<,1>,3<,4<,4>,5>,7<,7>,8>,10<,10>,12<,12>,13>,14>,16<,16>,18<,18>,20<,20>,21>,23<,23>,25<,25>,27<,27>,28>,30<,31<,31>,32>,33>,34>,35>,36>,38<,38>,39>,40>,41>,43<,43>,
        """;

    [CFact]
    public void StringOrder_Ordinal_MatchDotnet()
    {
        Assert.Equal(StringOrderExpected.ReplaceLineEndings("\n"),
            Backends.AssertParity(StringOrderProgram, "P"));
    }

    // Lua の `<` は strcoll なので collate locale で順序が変わる。
    // runtime の順序は locale に依らない
    [Fact]
    public void StringOrder_Lua_IgnoresCollateLocale()
    {
        var output = Backends.RunLua([StringOrderProgram], "P", """
            assert(os.setlocale("en_US.UTF-8", "collate"), "no en_US.UTF-8 locale")
            assert("a" < "B", "the collate locale does not change string <")
            """);
        Assert.Equal(StringOrderExpected.ReplaceLineEndings("\n"), output);
    }
}
