namespace TinyCs.Tcs2c.Tests;

// GC semantics tests (T252: frame-synchronized generational GC). GC runs only at frame boundaries
// (the point of returning from a lib-form entry / tcs_lib_gc), so this runs Setup / Frame×N / Report in lib form
// and verifies that objects held through each root (static / host hold) and each container kind / write barrier
// path survive across frames, and that unreachable garbage
// is freed at boundaries so the heap stays bounded, requiring the same stdout as the VM and .NET even under stress (an old-generation full
// GC + 256-byte nursery chunks at every boundary).
public class GcTests
{
    [CFact]
    public void Churn_KeepsLiveGraph_AcrossFrames()
    {
        // Grows a static chain / List while creating lots of garbage every frame.
        // References from the chain head (young) → the previous frame's head (old) cross the boundary
        const string source = """
            using TinySystem.Text;
            using System.Collections.Generic;
            public class Node
            {
                public int V;
                public Node Next;
                public string Tag;
                public Node(int v, Node next) { V = v; Next = next; Tag = "n" + v.ToString(); }
            }
            public class Churn
            {
                public static List<int> Keep = new List<int>();
                public static Node Head;
                public static int Sum;
                public static int Frames;
                public static void Setup() { Sum = 0; Frames = 0; Head = null; Keep.Clear(); }
                public static void Frame()
                {
                    int sum = Sum;
                    for (int i = 0; i < 2000; i++)
                    {
                        var n = new Node(i, i % 500 == 0 ? Head : null);
                        if (i % 500 == 0) Head = n;
                        var arr = new int[16];
                        arr[3] = i;
                        var l = new List<float>();
                        l.Add(1.5f);
                        l.Add((float)i);
                        var s = "x" + i.ToString();
                        sum = sum + arr[3] % 7 + (i % 2 == 0 ? 1 : 0) + s.RuneCount();
                    }
                    Sum = sum;
                    Frames = Frames + 1;
                    if (Frames % 5 == 0) Keep.Add(sum);
                }
                public static void Report()
                {
                    int chain = 0;
                    int sum = Sum;
                    var c = Head;
                    while (c != null) { chain = chain + 1; sum = sum + c.Tag.RuneCount(); c = c.Next; }
                    System.Console.WriteLine(sum);
                    System.Console.WriteLine(chain);
                    System.Console.WriteLine(Keep.Count);
                }
                public static void Main()
                {
                    Setup();
                    for (int f = 0; f < 10; f++) Frame();
                    Report();
                }
            }
            """;
        var expected = Backends.AssertParityLib(source, "Churn", 10);
        Assert.Equal(expected, Backends.AssertParity(source, "Churn"));
    }

    [CFact]
    public void Roots_AllContainerKinds_SurviveFrames_WithWriteBarriers()
    {
        // Into each slot of the graph promoted to the old generation in Setup (class field / struct-in-class /
        // struct array / ref array / List / Dict value / closure cell / static struct),
        // writes young objects every frame. A missing barrier shows up as the young object
        // not being promoted at the next boundary and disappearing when the nursery is reset
        const string source = """
            using TinySystem.Text;
            using System;
            using System.Collections.Generic;
            public struct Slot
            {
                public int Id;
                public string Name;
                public Payload Data;
                public void Rename(string n) { Name = n; }
            }
            public class Payload { public string Text; public int[] Nums; public Payload(string t) { Text = t; Nums = new int[4]; Nums[2] = t.RuneCount(); } }
            public class Box { public Payload Inner; }
            public class Holder
            {
                public Box Only = new Box();
                public Slot Primary;
                public Slot[] Slots = new Slot[3];
                public Payload[] Refs = new Payload[2];
                public List<Payload> Items = new List<Payload>();
                public Dictionary<string, Payload> ByName = new Dictionary<string, Payload>();
                public Dictionary<int, Slot> ById = new Dictionary<int, Slot>();
                public Func<string> Thunk;
                public Payload Latest;
            }
            public class Program
            {
                public static Holder Static;
                public static Slot StaticSlot;
                public static int Frames;
                public static void Setup()
                {
                    var h = new Holder();
                    h.Primary.Id = 1; h.Primary.Name = "pri" + "mary"; h.Primary.Data = new Payload("alpha");
                    var s1 = new Slot(); s1.Name = "slot" + 1.ToString(); s1.Data = new Payload("beta");
                    h.Slots[1] = s1;
                    h.Slots[2].Name = "s" + 2.ToString();
                    h.Refs[0] = new Payload("gamma");
                    h.Latest = new Payload("latest");
                    h.Only.Inner = new Payload("inner");
                    h.ByName["k" + 1.ToString()] = new Payload("delta");
                    var s = new Slot(); s.Name = "by" + "id"; s.Data = new Payload("epsilon");
                    h.ById[7] = s;
                    var captured = new Payload("zeta");
                    h.Thunk = () => { captured = new Payload(captured.Text + "+"); return captured.Text; };
                    Static = h;
                    StaticSlot.Data = new Payload("eta");
                    Frames = 0;
                    Check = 0;
                }
                public static int Check;
                public static void Frame()
                {
                    var g = Static;
                    var f = Frames.ToString();
                    // Reads the young objects written in the previous frame (after the nursery has been reused)
                    Check = Check + g.Primary.Data.Text.RuneCount() + g.Primary.Name.RuneCount()
                        + g.Slots[1].Data.Text.RuneCount() + g.Slots[2].Name.RuneCount() + g.Refs[0].Text.RuneCount()
                        + g.Items.Count + g.ByName.Count + g.ById[7].Data.Text.RuneCount()
                        + g.Latest.Text.RuneCount() + g.Only.Inner.Text.RuneCount() + StaticSlot.Data.Text.RuneCount();
                    g.Primary.Data = new Payload("alpha" + f);
                    g.Primary.Rename("primary" + f);
                    g.Slots[1].Data = new Payload("beta" + f);
                    g.Slots[2].Name = "s2" + f;
                    g.Refs[0] = new Payload("gamma" + f);
                    g.Items.Add(new Payload("item" + f));
                    g.ByName["k" + f] = new Payload("delta" + f);
                    var s = g.ById[7];
                    s.Data = new Payload("epsilon" + f);
                    g.ById[7] = s;
                    g.Latest = new Payload("latest" + f);
                    g.Only.Inner = new Payload("inner" + f);
                    var th = g.Thunk;
                    th();
                    StaticSlot.Data = new Payload("eta" + f);
                    Frames = Frames + 1;
                    int junk = 0;
                    for (int i = 0; i < 1000; i++)
                    {
                        var p = new Payload("junk" + i.ToString());
                        var d = new Dictionary<string, int>();
                        d[p.Text] = i;
                        junk = junk + p.Nums[2] + d.Count;
                    }
                    if (junk < 0) Console.WriteLine(junk);
                }
                public static void Report()
                {
                    var g = Static;
                    Console.WriteLine(g.Primary.Name + ":" + g.Primary.Data.Text + ":" + g.Primary.Data.Nums[2].ToString());
                    Console.WriteLine(g.Slots[1].Name + ":" + g.Slots[1].Data.Text + ":" + g.Slots[2].Name);
                    Console.WriteLine(g.Refs[0].Text);
                    Console.WriteLine(g.Items.Count.ToString() + ":" + g.Items[0].Text + ":" + g.Items[g.Items.Count - 1].Text);
                    Console.WriteLine(g.ByName.Count.ToString() + ":" + g.ByName["k1"].Text + ":" + g.ByName["k0"].Text);
                    Console.WriteLine(g.ById[7].Name + ":" + g.ById[7].Data.Text);
                    Console.WriteLine(g.Latest.Text + ":" + g.Only.Inner.Text);
                    var th = g.Thunk;
                    Console.WriteLine(th());
                    Console.WriteLine(StaticSlot.Data.Text);
                    Console.WriteLine(Frames);
                    Console.WriteLine(Check);
                }
                public static void Main()
                {
                    Setup();
                    for (int f = 0; f < 6; f++) Frame();
                    Report();
                }
            }
            """;
        var expected = Backends.AssertParityLib(source, "Program", 6);
        Assert.Equal(expected, Backends.AssertParity(source, "Program"));
    }

    [CFact]
    public void Dictionary_GrowthRehash_KeepsYoungNodesBehindOldOnes()
    {
        // Path where rehash relinks a node already promoted to the old generation in front of a young node
        const string source = """
            using System;
            using System.Collections.Generic;
            public class Program
            {
                public static Dictionary<int, int> Map = new Dictionary<int, int>();
                public static int Frames;
                public static void Setup() { Map = new Dictionary<int, int>(); Frames = 0; }
                public static void Frame()
                {
                    for (int i = 0; i < 17; i++) Map[Frames * 32 + i] = Frames * 32 + i;
                    Frames = Frames + 1;
                }
                public static void Report()
                {
                    int sum = 0;
                    for (int f = 0; f < Frames; f++)
                        for (int i = 0; i < 17; i++) sum += Map[f * 32 + i];
                    Console.WriteLine(Map.Count + ":" + sum);
                }
            }
            """;
        Backends.AssertParityLib(source, "Program", 6);
    }

    [CFact]
    public void Dictionary_Remove_UnlinksBetweenYoungAndOldNodes()
    {
        // remove relinks next without a barrier. Removes both a young node (a key from this frame) and
        // an old node (a key from earlier frames) from a chain where they are mixed, also across grow
        const string source = """
            using TinySystem.Text;
            using System;
            using System.Collections.Generic;
            public class Program
            {
                public static Dictionary<int, string> Map = new Dictionary<int, string>();
                public static int Frames;
                public static void Setup() { Map = new Dictionary<int, string>(); Frames = 0; }
                public static void Frame()
                {
                    for (int i = 0; i < 12; i++) Map[Frames * 100 + i] = "v" + (Frames * 100 + i).ToString();
                    if (Frames > 0) for (int i = 0; i < 12; i += 2) Map.Remove((Frames - 1) * 100 + i);
                    for (int i = 1; i < 12; i += 4) Map.Remove(Frames * 100 + i);
                    Frames = Frames + 1;
                }
                public static void Report()
                {
                    int sum = 0;
                    for (int f = 0; f < Frames; f++)
                        for (int i = 0; i < 12; i++)
                            if (Map.TryGetValue(f * 100 + i, out var v)) sum += f * 100 + i + v.RuneCount();
                    Console.WriteLine(Map.Count + ":" + sum);
                }
            }
            """;
        Backends.AssertParityLib(source, "Program", 8);
    }

    [CFact]
    public void Garbage_IsReclaimed_AtFrameBoundary_HeapStaysBounded()
    {
        // Every frame, ~2 MB of unreachable allocations + 64 KB held in a static. At the boundary after returning from the
        // entry, the nursery goes back to one chunk and only the held part is promoted; at the boundary where the promoted
        // bytes exceed the threshold (1 MiB), an old-generation mark-sweep runs and the survivors
        // fit in one held part (the host-side statistic tcs_lib_heap_bytes returns the same value)
        const string source = """
            public class Blob { public int[] Data; public Blob(int n) { Data = new int[n]; } }
            public class Program
            {
                public static Blob Last;
                public static int Acc;
                public static void Frame()
                {
                    int acc = Acc;
                    for (int i = 0; i < 2000; i++)
                    {
                        var b = new Blob(256);
                        b.Data[i % 256] = i;
                        acc = acc + b.Data[i % 256] % 3;
                    }
                    Last = new Blob(16384);
                    Last.Data[100] = acc;
                    Acc = acc;
                }
                public static void Report()
                {
                    System.Console.WriteLine(Acc);
                    System.Console.WriteLine(Last.Data[100] == Acc);
                }
            }
            """;
        var c = Backends.EmitC([source], null, lib: true);
        // Adds the host to the same translation unit to read the runtime's internal statistics
        c += """
            int main(void)
            {
                int i;
                tcs_lib_init();
                for (i = 0; i < 50; i++) tcs_entry_Program_Frame();
                tcs_entry_Program_Report();
                tcs_lib_collect();
                printf("frames=%zu collections=%zu promoted=%zu live=%zu chunks=%zu nursery=%zu heap=%zu objects=%zu\n",
                    tcs_gc_frames, tcs_gc_collections, tcs_gc_promoted_bytes,
                    tcs_gc_live_bytes, tcs_gc_nursery_chunks, tcs_gc_nursery_bytes,
                    tcs_lib_heap_bytes(), tcs_lib_heap_objects());
                return 0;
            }
            """;
        var lines = Backends.CompileAndRunC(c).Split('\n');
        Assert.Equal("99950", lines[0]);
        Assert.Equal("True", lines[1]);
        var stats = lines[2].Split(' ')
            .Select(kv => kv.Split('='))
            .ToDictionary(kv => kv[0], kv => long.Parse(kv[1]));
        // The init + 50 frame + report entries and the explicit collect are boundaries
        Assert.Equal(53, stats["frames"]);
        Assert.Equal(1, stats["chunks"]);
        Assert.Equal(0, stats["nursery"]);
        // 50 × (64 KB + Blob) promoted ≈ 3.3 MB (garbage is not promoted)
        Assert.InRange(stats["promoted"], 50L * 65536, 50L * 65536 + 50 * 1024);
        Assert.True(stats["collections"] >= 2, $"expected old-gen collections, got {stats["collections"]}");
        Assert.True(stats["live"] < 3 * 65536, $"live bytes after last GC too large: {stats["live"]}");
        // After the explicit collect, the host statistic = only the old-generation survivors (one Last + Blob)
        Assert.True(stats["heap"] < 3 * 65536, $"heap bytes too large: {stats["heap"]}");
        Assert.InRange(stats["objects"], 2, 4);
    }

    [CFact]
    public void LibMode_EntryBoundaries_CollectAndRetainStatics()
    {
        // --lib: the main of the host (a separate translation unit) calls the entries repeatedly.
        // Objects held in statics survive across boundaries, and the host signals
        // boundaries with tcs_lib_gc
        const string source = """
            using TinySystem.Text;
            using System.Collections.Generic;
            public class State
            {
                public static List<int> Log = new List<int>();
                public static string Last;
                public static void Step()
                {
                    for (int i = 0; i < 500; i++)
                    {
                        var s = "step" + i.ToString();
                        if (i == 499) Last = s;
                    }
                    Log.Add(Last.RuneCount());
                }
                public static void Report()
                {
                    System.Console.WriteLine(Log.Count);
                    System.Console.WriteLine(Last);
                }
            }
            """;
        var c = Backends.EmitC([source], null, lib: true);
        const string host = """
            void tcs_lib_init(void);
            void tcs_lib_gc(void);
            void tcs_entry_State_Step(void);
            void tcs_entry_State_Report(void);
            int main(void)
            {
                int i;
                tcs_lib_init();
                for (i = 0; i < 3; i++) {
                    tcs_entry_State_Step();
                    tcs_lib_gc();
                }
                tcs_entry_State_Report();
                return 0;
            }
            """;
        var output = Backends.CompileAndRunC(c, stress: true, extraCSource: host);
        Assert.Equal("3\nstep499", output);
    }

    [CFact]
    public void HostHold_KeepsObjectAcrossFrames_UntilRelease()
    {
        // Pointers that the host holds outside an entry can cross a boundary only through a slot registered with tcs_lib_hold
        // (they are rewritten to the promotion target). Removes the object from the static, keeps it only through hold,
        // crosses stress boundaries (old-generation full GC + nursery overwrite) several
        // times, then puts it back and reads it
        const string source = """
            using TinySystem.Text;
            public class State
            {
                public static string Last;
                public static void Setup() { Last = "held" + 42.ToString(); }
                public static void Frame()
                {
                    for (int i = 0; i < 300; i++)
                    {
                        var s = "junk" + i.ToString();
                        if (s.RuneCount() > 100) Last = s;
                    }
                }
                public static void Report() { System.Console.WriteLine(Last); }
            }
            """;
        var c = Backends.EmitC([source], null, lib: true);
        c += """
            int main(void)
            {
                int i;
                void *held;
                tcs_lib_init();
                tcs_entry_State_Setup();
                held = tcs_s_State_Last;
                tcs_lib_hold(&held);
                tcs_s_State_Last = NULL;
                for (i = 0; i < 4; i++) {
                    tcs_entry_State_Frame();
                    tcs_lib_gc();
                }
                tcs_s_State_Last = held;
                tcs_lib_release(&held);
                tcs_lib_gc();
                tcs_entry_State_Report();
                return 0;
            }
            """;
        Assert.Equal("held42", Backends.CompileAndRunC(c, stress: true));
        Assert.Equal("held42", Backends.CompileAndRunC(c));
    }

    [CFact]
    public void Stress_PoisonsResetNursery_SoStaleReadsDoNotLookValid()
    {
        // A stale pointer that crossed a boundary (a symptom of a missing barrier / root) reads, under stress,
        // a poison value rather than 0 or the original value. If 0 could be read, the leak would not show in the output and would be missed
        const string stub = """
            namespace H { public class Ev { public int Kind; } }
            """;
        const string source = """
            public static class G { public static void OnEv(H.Ev e) { } }
            """;
        var exported = IlExport.Export([source], specializeGenerics: true,
            referenceSources: [stub]);
        Assert.Empty(exported.Diagnostics);
        var c = SelfHostC.Emit([source], null, lib: true, referenceSources: [stub]);
        const string host = """

            int main(void)
            {
                Tcs_H_Ev *stale;
                tcs_lib_init();
                tcs_lib_gc();
                stale = tcs_new_H_Ev();
                stale->f_kind = 5;
                tcs_lib_gc();
                printf("%d\n", (int)stale->f_kind);
                return 0;
            }
            """;
        var read = Backends.CompileAndRunC(c + host, stress: true);
        Assert.NotEqual("0", read);
        Assert.NotEqual("5", read);
    }

    [CFact]
    public void StringLiterals_AreStaticObjects_NotPerEvaluation()
    {
        const string source = """
            using TinySystem.Text;
            public class Program
            {
                public static string Pick(int i) => i % 2 == 0 ? "even" : "odd";
                public static void Main()
                {
                    int n = 0;
                    for (int i = 0; i < 10; i++) n = n + Pick(i).RuneCount();
                    System.Console.WriteLine(n);
                    System.Console.WriteLine(Pick(4) == Pick(2));
                }
            }
            """;
        var c = Backends.EmitC([source], "Program");
        Assert.Contains("TCS_GC_STATIC", c);
        Assert.DoesNotContain("tcs_string_new((const unsigned char *)\"\\x65", c);
        Assert.Equal("35\nTrue", Backends.CompileAndRunC(c, stress: true));
        Assert.Equal("35\nTrue", Backends.RunVm([source], "Program"));
    }

    [CFact]
    public void LibMode_FaultHandlerUnwindsAndTheNextEntryRuns()
    {
        // The host's fault handler (tcs_lib_on_fault) exits with longjmp, and the next entry runs after tcs_lib_unwind.
        // Objects put into statics before the fault survive the boundary GC, and the nesting depth is restored, so the boundary is hit for every entry
        const string source = """
            using System.Collections.Generic;
            public class Program
            {
                public static List<string> Kept = new List<string>();
                public static int Zero;
                public static void Frame(int n)
                {
                    Kept.Add("f" + n);
                    if (n % 2 == 1) Kept.Add("unreachable " + n / Zero);
                }
                public static void Report()
                {
                    System.Console.WriteLine(string.Join(" ", Kept));
                }
            }
            """;
        var c = Backends.EmitC([source], null, lib: true);
        c += """
            #include <setjmp.h>
            static jmp_buf host_jmp;
            static int host_faults;
            static void host_fault(const char *kind) { printf("fault %s\n", kind); host_faults++; longjmp(host_jmp, 1); }
            int main(void)
            {
                volatile int i;
                tcs_lib_init();
                tcs_lib_on_fault(host_fault);
                for (i = 0; i < 4; i++) {
                    if (setjmp(host_jmp) == 0) tcs_entry_Program_Frame(i);
                    else tcs_lib_unwind();
                }
                tcs_entry_Program_Report();
                printf("faults=%d frames=%zu\n", host_faults, tcs_gc_frames);
                return 0;
            }
            """;
        foreach (var stress in new[] { false, true })
        {
            var lines = Backends.CompileAndRunC(c, stress: stress).Split('\n');
            Assert.Equal("fault divide-by-zero", lines[0]);
            Assert.Equal("fault divide-by-zero", lines[1]);
            Assert.Equal("f0 f1 f2 f3", lines[2]);
            // init + 4 Frames (the 2 that faulted are tcs_lib_unwind boundaries) + Report
            Assert.Equal("faults=2 frames=6", lines[3]);
        }
    }
}
