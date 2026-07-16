namespace TinyCs.Tests;

public class LinqSemanticTests
{
    [Fact]
    public void Linq_All()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static bool Test()
                {
                    var list = new List<int> { 2, 4, 6 };
                    return list.All(x => x % 2 == 0);
                }
            }
            """, "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void Linq_FirstOrDefault_Found()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 1, 2, 3 };
                    return list.FirstOrDefault(x => x > 1);
                }
            }
            """, "T.Test()");
        Assert.Equal("2", result);
    }

    [Fact]
    public void Linq_Min()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 5, 3, 8, 1 };
                    return list.Min();
                }
            }
            """, "T.Test()");
        Assert.Equal("1", result);
    }

    [Fact]
    public void Linq_Max()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 5, 3, 8, 1 };
                    return list.Max();
                }
            }
            """, "T.Test()");
        Assert.Equal("8", result);
    }

    [Fact]
    public void Linq_Count()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 1, 2, 3 };
                    return list.Count();
                }
            }
            """, "T.Test()");

        Assert.Equal("3", result);
    }

    [Fact]
    public void Linq_Count_WithPredicate()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 1, 2, 3, 4 };
                    return list.Count(x => x % 2 == 0);
                }
            }
            """, "T.Test()");

        Assert.Equal("2", result);
    }

    [Fact]
    public void Linq_ToDictionary_KeySelector()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;

            public class Item
            {
                public string Name;
                public int Count;

                public Item(string name, int count)
                {
                    Name = name;
                    Count = count;
                }
            }

            public class T
            {
                public static int Test()
                {
                    var items = new List<Item>
                    {
                        new Item("Potion", 5),
                        new Item("Arrow", 20)
                    };
                    var byName = items.ToDictionary(item => item.Name);
                    return byName["Arrow"].Count;
                }
            }
            """, "T.Test()");

        Assert.Equal("20", result);
    }

    [Fact]
    public void Linq_ToDictionary_ValueSelector()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;

            public class Item
            {
                public string Name;
                public int Count;

                public Item(string name, int count)
                {
                    Name = name;
                    Count = count;
                }
            }

            public class T
            {
                public static int Test()
                {
                    var items = new List<Item>
                    {
                        new Item("Potion", 5),
                        new Item("Arrow", 20)
                    };
                    var counts = items.ToDictionary(item => item.Name, item => item.Count);
                    return counts["Potion"];
                }
            }
            """, "T.Test()");

        Assert.Equal("5", result);
    }

    [Fact]
    public void Linq_OrderByDescending()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static string Test()
                {
                    var list = new List<int> { 2, 4, 1, 3 };
                    var sorted = list.OrderByDescending(x => x).ToList();
                    return sorted[0].ToString() + "," + sorted[1].ToString();
                }
            }
            """, "T.Test()");

        Assert.Equal("4,3", result);
    }

    [Fact]
    public void Linq_Take()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static string Test()
                {
                    var list = new List<int> { 1, 2, 3, 4 };
                    var taken = list.Take(2).ToList();
                    return taken.Count.ToString() + ":" + taken[0].ToString() + "," + taken[1].ToString();
                }
            }
            """, "T.Test()");

        Assert.Equal("2:1,2", result);
    }

    [Fact]
    public void Linq_Skip()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static string Test()
                {
                    var list = new List<int> { 1, 2, 3, 4 };
                    var skipped = list.Skip(2).ToList();
                    return skipped.Count.ToString() + ":" + skipped[0].ToString() + "," + skipped[1].ToString();
                }
            }
            """, "T.Test()");

        Assert.Equal("2:3,4", result);
    }

    [Fact]
    public void Linq_Last()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 1, 2, 3, 4 };
                    return list.Last(x => x % 2 == 0);
                }
            }
            """, "T.Test()");

        Assert.Equal("4", result);
    }

    [Fact]
    public void Linq_LastOrDefault_NotFound()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static string Test()
                {
                    var list = new List<string> { "a", "b" };
                    var found = list.LastOrDefault(x => x == "z");
                    return found == null ? "nil" : found;
                }
            }
            """, "T.Test()");

        Assert.Equal("nil", result);
    }

    // T152: empty sequence の default は要素型別 (int=0 / bool=false / ref=nil)。
    // First/Last/Min/Max の empty・predicate miss は nil ではなく明示 error。
    [Fact]
    public void Linq_FirstOrDefault_ValueTypeDefaults()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static string Test()
                {
                    var ints = new List<int>();
                    var bools = new List<bool>();
                    var strs = new List<string>();
                    var i = ints.FirstOrDefault();
                    var m = ints.FirstOrDefault(x => x > 10);
                    var b = bools.FirstOrDefault();
                    var s = strs.FirstOrDefault();
                    var l = ints.LastOrDefault();
                    return $"{i}|{m}|{b}|{s ?? "nil"}|{l}";
                }
            }
            """, "T.Test()");

        Assert.Equal("0|0|false|nil|0", result);
    }

    [Fact]
    public void Linq_FirstOnEmpty_RaisesExplicitError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TestHelper.TranspileAndRunWithRuntime("""
                using System.Collections.Generic;
                using System.Linq;
                public class T
                {
                    public static int Test()
                    {
                        var ints = new List<int>();
                        return ints.First();
                    }
                }
                """, "T.Test()"));

        Assert.Contains("Sequence contains no elements", ex.Message);
    }

    [Fact]
    public void Linq_MinMaxOnEmpty_RaiseExplicitError()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            TestHelper.TranspileAndRunWithRuntime("""
                using System.Collections.Generic;
                using System.Linq;
                public class T
                {
                    public static int Test()
                    {
                        var ints = new List<int>();
                        return ints.Min();
                    }
                }
                """, "T.Test()"));

        Assert.Contains("Sequence contains no elements", ex.Message);
    }

    // T153: ToDictionary の key/value selector は各要素 1 回だけ、key → value
    // の順で評価される (C# と同じ評価回数・順序)。
    [Fact]
    public void Linq_ToDictionary_SelectorsEvaluatedOncePerElementInOrder()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static string Log = "";

                public static string Test()
                {
                    var names = new List<string> { "a", "bb" };
                    var d = names.ToDictionary(
                        x => { Log = Log + "k"; return x; },
                        x => { Log = Log + "v"; return x.Length; });
                    return $"{Log}|{d["a"]}|{d["bb"]}";
                }
            }
            """, "T.Test()");

        Assert.Equal("kvkv|1|2", result);
    }

    [Fact]
    public void Linq_SumOnEmpty_ReturnsZero()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static int Test()
                {
                    var ints = new List<int>();
                    return ints.Sum();
                }
            }
            """, "T.Test()");

        Assert.Equal("0", result);
    }
}
