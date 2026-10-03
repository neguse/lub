namespace TinyCs.Tcs2c.Tests;

// GC の意味論テスト (T252: フレーム同期の世代別 GC)。GC はフレーム境界
// (lib 形の entry から戻った点 / tcs_lib_gc) でだけ走るので、lib 形で Setup / Frame×N / Report
// を回し、各 root (static / host hold) と各 container 種別 / ライトバリア
// 経路で保持した object がフレームを跨いで生きること、到達不能な garbage が
// 境界で解放されて heap が有界に保たれることを、stress (毎境界で旧世代 full
// GC + 256 byte nursery chunk) でも Lua と同じ stdout を要求して検証する。
public class GcTests
{
    [CFact]
    public void Churn_KeepsLiveGraph_AcrossFrames()
    {
        // 毎フレーム大量の garbage を作りつつ static の chain / List を育てる。
        // chain の先頭 (若い) → 前フレームの先頭 (旧) の参照が境界を跨ぐ
        const string source = """
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
                        sum = sum + arr[3] % 7 + (i % 2 == 0 ? 1 : 0) + s.Length;
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
                    while (c != null) { chain = chain + 1; sum = sum + c.Tag.Length; c = c.Next; }
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
        // Setup で旧世代へ昇格した graph の各 slot (class field / struct-in-class /
        // struct 配列 / ref 配列 / List / Dict 値 / 閉包 cell / static struct) へ
        // 毎フレーム若い object を書く。バリア漏れは次の境界で若い object が
        // 昇格されず nursery のリセットで消える形で現れる
        const string source = """
            using System;
            using System.Collections.Generic;
            public struct Slot
            {
                public int Id;
                public string Name;
                public Payload Data;
                public void Rename(string n) { Name = n; }
            }
            public class Payload { public string Text; public int[] Nums; public Payload(string t) { Text = t; Nums = new int[4]; Nums[2] = t.Length; } }
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
                    // 前フレームに書いた若い object を (nursery が再利用された後に) 読む
                    Check = Check + g.Primary.Data.Text.Length + g.Primary.Name.Length
                        + g.Slots[1].Data.Text.Length + g.Slots[2].Name.Length + g.Refs[0].Text.Length
                        + g.Items.Count + g.ByName.Count + g.ById[7].Data.Text.Length
                        + g.Latest.Text.Length + g.Only.Inner.Text.Length + StaticSlot.Data.Text.Length;
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
        // 旧世代に昇格済みの node を rehash で若い node の前へ繋ぎ直す経路
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
    public void Garbage_IsReclaimed_AtFrameBoundary_HeapStaysBounded()
    {
        // 毎フレーム ~2 MB の到達不能な確保 + 64 KB の static 保持。entry から
        // 戻った境界で nursery は chunk 1 個に戻り、昇格は保持分だけ、昇格
        // bytes が閾値 (1 MiB) を超えた境界で旧世代 mark-sweep が走り生存は
        // 1 個分に収まる (host 側の統計 tcs_lib_heap_bytes も同じ値を返す)
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
        // host を同じ translation unit に足して runtime 内部の統計を読む
        c += """
            int main(void)
            {
                int i;
                tcs_lib_init();
                for (i = 0; i < 50; i++) tcs_entry_Program_frame();
                tcs_entry_Program_report();
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
        Assert.Equal("true", lines[1]);
        var stats = lines[2].Split(' ')
            .Select(kv => kv.Split('='))
            .ToDictionary(kv => kv[0], kv => long.Parse(kv[1]));
        // init + 50 frame + report の各 entry と明示 collect が境界
        Assert.Equal(53, stats["frames"]);
        Assert.Equal(1, stats["chunks"]);
        Assert.Equal(0, stats["nursery"]);
        // 50 × (64 KB + Blob) の昇格 ≈ 3.3 MB (garbage は昇格されない)
        Assert.InRange(stats["promoted"], 50L * 65536, 50L * 65536 + 50 * 1024);
        Assert.True(stats["collections"] >= 2, $"expected old-gen collections, got {stats["collections"]}");
        Assert.True(stats["live"] < 3 * 65536, $"live bytes after last GC too large: {stats["live"]}");
        // 明示 collect の後は host 統計 = 旧世代の生存 (Last 1 個 + Blob) だけ
        Assert.True(stats["heap"] < 3 * 65536, $"heap bytes too large: {stats["heap"]}");
        Assert.InRange(stats["objects"], 2, 4);
    }

    [CFact]
    public void LibMode_EntryBoundaries_CollectAndRetainStatics()
    {
        // --lib: host (別 translation unit) の main から entry を繰り返し呼ぶ。
        // static に保持した object は境界を跨いで生き、host は tcs_lib_gc で
        // 境界を知らせる
        const string source = """
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
                    Log.Add(Last.Length);
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
            void tcs_entry_State_step(void);
            void tcs_entry_State_report(void);
            int main(void)
            {
                int i;
                tcs_lib_init();
                for (i = 0; i < 3; i++) {
                    tcs_entry_State_step();
                    tcs_lib_gc();
                }
                tcs_entry_State_report();
                return 0;
            }
            """;
        var output = Backends.CompileAndRunC(c, stress: true, extraCSource: host);
        Assert.Equal("3\nstep499", output);
    }

    [CFact]
    public void HostHold_KeepsObjectAcrossFrames_UntilRelease()
    {
        // host が entry の外で持つ pointer は tcs_lib_hold した slot 経由でだけ
        // 境界を跨げる (昇格先へ書き換わる)。static から外して hold だけで
        // 保持し、stress の境界 (旧世代 full GC + nursery 上書き) を複数回
        // 跨いだ後に戻して読む
        const string source = """
            public class State
            {
                public static string Last;
                public static void Setup() { Last = "held" + 42.ToString(); }
                public static void Frame()
                {
                    for (int i = 0; i < 300; i++)
                    {
                        var s = "junk" + i.ToString();
                        if (s.Length > 100) Last = s;
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
                tcs_entry_State_setup();
                held = tcs_s_State_last;
                tcs_lib_hold(&held);
                tcs_s_State_last = NULL;
                for (i = 0; i < 4; i++) {
                    tcs_entry_State_frame();
                    tcs_lib_gc();
                }
                tcs_s_State_last = held;
                tcs_lib_release(&held);
                tcs_lib_gc();
                tcs_entry_State_report();
                return 0;
            }
            """;
        Assert.Equal("held42", Backends.CompileAndRunC(c, stress: true));
        Assert.Equal("held42", Backends.CompileAndRunC(c));
    }

    [CFact]
    public void StringLiterals_AreStaticObjects_NotPerEvaluation()
    {
        const string source = """
            public class Program
            {
                public static string Pick(int i) => i % 2 == 0 ? "even" : "odd";
                public static void Main()
                {
                    int n = 0;
                    for (int i = 0; i < 10; i++) n = n + Pick(i).Length;
                    System.Console.WriteLine(n);
                    System.Console.WriteLine(Pick(4) == Pick(2));
                }
            }
            """;
        var c = Backends.EmitC([source], "Program");
        Assert.Contains("TCS_GC_STATIC", c);
        Assert.DoesNotContain("tcs_string_new((const unsigned char *)\"\\x65", c);
        Assert.Equal("35\ntrue", Backends.CompileAndRunC(c, stress: true));
        Assert.Equal("35\ntrue", Backends.RunLua([source], "Program"));
    }
}
