using System.Text.RegularExpressions;

namespace TinyCs.Tcs2c.Tests;

// Exhaustively enumerates the GC within bounds, using the real runtime as is (GcEnumeration.c). This harness
// program is compiled to C with --lib, the harness is appended to its end, and the result is built as one translation unit
// with stress. All mutator stores go through the functions emitted here,
// so the barrier placement of the emitter and the runtime is checked as is.
// The check is confirmed to work by broken variants, each dropping one barrier / root / promotion / mark site from the text of the generated C,
// all of which turn red with a witness.
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

    // Redirects the runtime's allocations to the harness arena (for snapshot / restore)
    private static readonly string[] ArenaFlags =
        ["-Dmalloc=h_malloc", "-Dcalloc=h_calloc", "-Drealloc=h_realloc", "-Dfree=h_free"];

    private static string HarnessSource() =>
        Backends.EmitC([Program], null, lib: true)
        + File.ReadAllText(Path.Combine(Backends.ProjectRoot, "tests", "tcs-backends", "GcEnumeration.c"));

    private static string Run(string c, int depth) =>
        Backends.CompileAndRunC(c, stress: true, extraFlags: [.. ArenaFlags, $"-DH_DEPTH={depth}"]);

    [CFact]
    public void Enumeration_FindsNoViolation_AndReachesBarrierHazards()
    {
        var output = Run(HarnessSource(), depth: 9);
        Assert.StartsWith("ok depth=9 ", output);
        var stats = output.Split(' ').Skip(1).Select(pair => pair.Split('='))
            .ToDictionary(pair => pair[0], pair => long.Parse(pair[1]));
        // That the enumeration actually hit the shapes that matter for the properties (so it is not an empty check that is always green)
        Assert.True(stats["frames"] > 0);
        Assert.True(stats["old_young_stores"] > 0, "no store of a young object into an old one");
        Assert.True(stats["grow_old"] > 0, "no rehash of a Dict holding old nodes");
        Assert.True(stats["remove_hazard"] > 0, "no barrier-free remove relinking an old owner to a young node");
    }

    // (name, regular expression that appears at least once in the generated C, replacement). On the runtime side, the C text of runtime/core;
    // on the emitter side, the barrier temp of EmitAssign in CBackend
    public static TheoryData<string, string, string> BrokenVariants => new()
    {
        { "emitter: class field store barrier", @"tcs_wb\(__tcs_object_\d+\);", "" },
        { "tcs_wb registers nothing", @"hdr->flags \|= TCS_GC_DIRTY;", "return;" },
        { "tcs_list_add barrier", @"if \(layout != NULL\)\s*tcs_wb\(list\);", "" },
        { "tcs_list_reserve barrier", @"(list->capacity = capacity;)\s*tcs_wb\(list\);", "$1" },
        { "tcs_dict_grow node barrier", @"tcs_wb\(node\);(\s*buckets\[bucket\] = node;)", "$1" },
        // tcs_wb(dict) at the end of tcs_dict_grow is subsumed by the barrier at the end of its only caller
        // tcs_dict_put, so dropping it causes no violation (GC section of the README)
        { "tcs_dict_put existing node barrier", @"\{\s*tcs_wb\(node\);\s*return node->value;\s*\}", "{ return node->value; }" },
        { "tcs_dict_put new node barrier", @"(dict->count\+\+;)\s*tcs_wb\(dict\);", "$1" },
        { "boundary: hold slot forwarding", @"tcs_gc_forward_slot\(tcs_gc_holds\[i\]\);", "(void)tcs_gc_holds[i];" },
        { "boundary: static forwarding", @"tcs_s_G_S = tcs_gc_promote\(tcs_s_G_S\);", "" },
        { "boundary: dirty flag clear", @"tcs_gc_dirty\[i\]->flags &= ~\(uint32_t\)TCS_GC_DIRTY;", "" },
        { "boundary: dirty object forwarding", @"tcs_gc_forward_object\(tcs_gc_dirty\[i\]\);", "" },
        { "boundary: scan queue drain", @"tcs_gc_forward_object\(tcs_gc_mark_stack\[--tcs_gc_mark_count\]\);", "--tcs_gc_mark_count;" },
        { "forward: list buffer", @"list->data = tcs_gc_promote\(list->data\);", "" },
        { "forward: dict node value", @"tcs_gc_forward_value\(node->value, hdr->layout\);", "" },
        { "mark: list buffer", @"tcs_gc_mark_ptr\(list->data\);", "" },
        // Marking of the node chain (node->next) can be dropped without a violation, because the insertion-order order visits every node (support-matrix §18).
        // Instead, drops the mark of the order buffer itself
        { "mark: dict order buffer", @"tcs_gc_mark_ptr\(dict->order\);", "" },
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
