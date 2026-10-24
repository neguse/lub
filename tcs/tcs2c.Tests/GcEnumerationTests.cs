using System.Text.RegularExpressions;

namespace TinyCs.Tcs2c.Tests;

// GC を本物の runtime のまま有界に全列挙する (GcEnumeration.c)。この harness
// program を --lib で C にし、末尾に harness を連結して 1 つの translation unit
// として stress build する。mutator の store は全部ここで emit された関数を通る
// ので、emitter と runtime のバリアの置き方がそのまま検査対象になる。
// 検査が効いていることは、バリア / root / 昇格 / mark の各 1 箇所を生成 C の
// 字面で落とした broken variant がすべて witness 付きで赤になることで確かめる。
public class GcEnumerationTests
{
    private const string Program = """
        using System.Collections.Generic;
        public class Node
        {
            public Node A;
            public List<Node> L;
            public List<int> I;
            public Dictionary<int, Node> D;
            public int V;
        }
        public static class G
        {
            public static Node S;
            public static Node New(int v) { var n = new Node(); n.V = v; return n; }
            public static void SetA(Node x, Node y) { x.A = y; }
            public static Node GetA(Node x) { return x.A; }
            public static void NewList(Node x) { x.L = new List<Node>(); }
            public static void ListAdd(Node x, Node y) { x.L.Add(y); }
            public static void ListRemoveFirst(Node x) { x.L.RemoveAt(0); }
            public static void NewIntList(Node x) { x.I = new List<int>(); }
            public static void IntListAdd(Node x, int v) { x.I.Add(v); }
            public static void NewDict(Node x) { x.D = new Dictionary<int, Node>(); }
            public static void DictPut(Node x, int k, Node y) { x.D[k] = y; }
            public static void DictRemove(Node x, int k) { x.D.Remove(k); }
            public static void DictGrow(Node x) { for (int i = 100; i < 133; i++) x.D[i] = null; }
        }
        """;

    // runtime の確保を harness の arena に向ける (snapshot / restore のため)
    private static readonly string[] ArenaFlags =
        ["-Dmalloc=h_malloc", "-Dcalloc=h_calloc", "-Drealloc=h_realloc", "-Dfree=h_free"];

    private static string HarnessSource() =>
        Backends.EmitC([Program], null, lib: true)
        + File.ReadAllText(Path.Combine(Backends.ProjectRoot, "tcs2c.Tests", "GcEnumeration.c"));

    private static string Run(string c, int depth) =>
        Backends.CompileAndRunC(c, stress: true, extraFlags: [.. ArenaFlags, $"-DH_DEPTH={depth}"]);

    [CFact]
    public void Enumeration_FindsNoViolation_AndReachesBarrierHazards()
    {
        var output = Run(HarnessSource(), depth: 9);
        Assert.StartsWith("ok depth=9 ", output);
        var stats = output.Split(' ').Skip(1).Select(pair => pair.Split('='))
            .ToDictionary(pair => pair[0], pair => long.Parse(pair[1]));
        // 列挙が性質に関わる形を実際に踏んだこと (常に緑の空検査でないこと)
        Assert.True(stats["frames"] > 0);
        Assert.True(stats["old_young_stores"] > 0, "no store of a young object into an old one");
        Assert.True(stats["grow_old"] > 0, "no rehash of a Dict holding old nodes");
        Assert.True(stats["remove_hazard"] > 0, "no barrier-free remove relinking an old owner to a young node");
    }

    // (名前, 生成 C に 1 回以上ある正規表現, 置換)。runtime 側は CRuntime.Gc /
    // CRuntime.Collections の字面、emitter 側は EmitAssign のバリア temp
    public static TheoryData<string, string, string> BrokenVariants => new()
    {
        { "emitter: class field store barrier", @"tcs_wb\(__tcs_object_\d+\);", "" },
        { "tcs_wb registers nothing", @"hdr->flags \|= TCS_GC_DIRTY;", "return;" },
        { "tcs_list_add barrier", @"if \(layout != NULL\) tcs_wb\(list\);", "" },
        { "tcs_list_reserve barrier", @"(list->capacity = capacity;)\s*tcs_wb\(list\);", "$1" },
        { "tcs_dict_grow node barrier", @"tcs_wb\(node\);(\s*buckets\[bucket\] = node;)", "$1" },
        // tcs_dict_grow 末尾の tcs_wb(dict) は唯一の呼び出し元 tcs_dict_put の末尾の
        // バリアに内包されていて、落としても違反は出ない (README の GC 節)
        { "tcs_dict_put existing node barrier", @"\{ tcs_wb\(node\); return node->value; \}", "{ return node->value; }" },
        { "tcs_dict_put new node barrier", @"(dict->count\+\+;)\s*tcs_wb\(dict\);", "$1" },
        { "boundary: hold slot forwarding", @"tcs_gc_forward_slot\(tcs_gc_holds\[i\]\);", "(void)tcs_gc_holds[i];" },
        { "boundary: static forwarding", @"tcs_s_G_S = tcs_gc_promote\(tcs_s_G_S\);", "" },
        { "boundary: dirty flag clear", @"tcs_gc_dirty\[i\]->flags &= ~\(uint32_t\)TCS_GC_DIRTY;", "" },
        { "boundary: dirty object forwarding", @"tcs_gc_forward_object\(tcs_gc_dirty\[i\]\);", "" },
        { "boundary: scan queue drain", @"tcs_gc_forward_object\(tcs_gc_mark_stack\[--tcs_gc_mark_count\]\);", "--tcs_gc_mark_count;" },
        { "forward: list buffer", @"list->data = tcs_gc_promote\(list->data\);", "" },
        { "forward: dict node value", @"tcs_gc_forward_value\(node->value, hdr->layout\);", "" },
        { "mark: list buffer", @"tcs_gc_mark_ptr\(list->data\);", "" },
        { "mark: dict node chain", @"tcs_gc_mark_ptr\(node->next\);", "" },
    };

    [CTheory]
    [MemberData(nameof(BrokenVariants))]
    public void Enumeration_CatchesEachBrokenVariant(string name, string pattern, string replacement)
    {
        var c = HarnessSource();
        var regex = new Regex(pattern);
        Assert.True(regex.IsMatch(c), $"{name}: pattern not found in the generated C");
        var failure = Assert.Throws<InvalidOperationException>(() => Run(regex.Replace(c, replacement), depth: 8));
        Assert.Contains("violation:", failure.Message);
        Assert.Contains("witness:", failure.Message);
    }
}
