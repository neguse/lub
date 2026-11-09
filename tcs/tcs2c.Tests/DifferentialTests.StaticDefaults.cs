namespace TinyCs.Tcs2c.Tests;

// 初期化子のない static field の意味論の 2 backend differential。期待値は同じ
// program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    // C# §15.5.6: static field は全部 default にしてから、初期化子のあるものだけを
    // 宣言順に実行する。先の初期化子が後ろの初期化子なし field に書いた値は残る
    [CFact]
    public void StaticInitializer_KeepsWriteToLaterFieldWithoutInitializer()
    {
        var output = Backends.AssertParity("""
            using System;
            public class Obj { public int V; public Obj(int v) { V = v; } }
            public class State
            {
                public static int First = Initialize();
                public static int Later;
                public static Obj Ref;
                public static int Prop { get; set; }
                public static bool Flag;
                public static int Explicit = 0;
                static int Initialize() { Later = 7; Ref = new Obj(4); Prop = 5; Flag = true; Explicit = 9; return 3; }
            }
            public class P
            {
                public static void Main()
                {
                    Console.WriteLine(State.First + "/" + State.Later + "/" + State.Ref.V + "/" + State.Prop + "/" + (State.Flag ? 1 : 0) + "/" + State.Explicit);
                }
            }
            """, "P");
        Assert.Equal("3/7/4/5/1/0", output);
    }
}
