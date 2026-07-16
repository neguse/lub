namespace TinyCs.Tests;

// T143: 副作用を含む lvalue (receiver / index に呼び出しがある compound
// assignment、??=、increment、collection mutation) は receiver / index を
// 一度だけ評価する。C# の評価回数・順序 (receiver → index → read → rhs →
// write) と一致させる。
public class LvalueEvaluationTests
{
    [Fact]
    public void CompoundAssignment_ElementAccess_ReceiverAndIndexOnce()
    {
        var result = TestHelper.TranspileAndRun("""
            using System.Collections.Generic;

            public class T
            {
                public static int GetCalls;
                public static int IdxCalls;
                public static List<int> Data;

                public static List<int> Get()
                {
                    GetCalls = GetCalls + 1;
                    return Data;
                }

                public static int Idx()
                {
                    IdxCalls = IdxCalls + 1;
                    return 1;
                }

                public static string Test()
                {
                    Data = new List<int> { 10, 20, 30 };
                    Get()[Idx()] += 5;
                    return $"{GetCalls}|{IdxCalls}|{Data[1]}";
                }
            }
            """, "T.Test()");
        Assert.Equal("1|1|25", result);
    }

    [Fact]
    public void CompoundAssignment_MemberAccess_ReceiverOnce()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Box
            {
                public int X = 10;
            }
            public class T
            {
                public static int Calls;
                public static Box B;

                public static Box Get()
                {
                    Calls = Calls + 1;
                    return B;
                }

                public static string Test()
                {
                    B = new Box();
                    Get().X += 3;
                    return $"{Calls}|{B.X}";
                }
            }
            """, "T.Test()");
        Assert.Equal("1|13", result);
    }

    [Fact]
    public void StringCompoundAssignment_Concatenates()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static string Test()
                {
                    var s = "a";
                    s += "b";
                    s += 1;
                    return s;
                }
            }
            """, "T.Test()");
        Assert.Equal("ab1", result);
    }

    [Fact]
    public void CoalesceAssignment_MemberAccess_ReceiverOnceAndRhsLazy()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Box
            {
                public string Label;
            }
            public class T
            {
                public static int GetCalls;
                public static int FbCalls;
                public static Box B;

                public static Box Get()
                {
                    GetCalls = GetCalls + 1;
                    return B;
                }

                public static string Fb()
                {
                    FbCalls = FbCalls + 1;
                    return "fb";
                }

                public static string Test()
                {
                    B = new Box();
                    Get().Label ??= Fb();
                    Get().Label ??= Fb();
                    return $"{GetCalls}|{FbCalls}|{B.Label}";
                }
            }
            """, "T.Test()");
        Assert.Equal("2|1|fb", result);
    }

    [Fact]
    public void Increment_ElementAccess_ReceiverAndIndexOnce()
    {
        var result = TestHelper.TranspileAndRun("""
            using System.Collections.Generic;

            public class T
            {
                public static int GetCalls;
                public static int IdxCalls;
                public static List<int> Data;

                public static List<int> Get()
                {
                    GetCalls = GetCalls + 1;
                    return Data;
                }

                public static int Idx()
                {
                    IdxCalls = IdxCalls + 1;
                    return 0;
                }

                public static string Test()
                {
                    Data = new List<int> { 5 };
                    Get()[Idx()]++;
                    return $"{GetCalls}|{IdxCalls}|{Data[0]}";
                }
            }
            """, "T.Test()");
        Assert.Equal("1|1|6", result);
    }

    [Fact]
    public void ListClear_SideEffectReceiver_EvaluatedOnce()
    {
        var result = TestHelper.TranspileAndRun("""
            using System.Collections.Generic;

            public class T
            {
                public static int Calls;
                public static List<int> Data;

                public static List<int> Get()
                {
                    Calls = Calls + 1;
                    return Data;
                }

                public static string Test()
                {
                    Data = new List<int> { 1, 2, 3 };
                    Get().Clear();
                    return $"{Calls}|{Data.Count}";
                }
            }
            """, "T.Test()");
        Assert.Equal("1|0", result);
    }
}
