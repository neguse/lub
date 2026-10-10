namespace TinyCs.Tcs2c.Tests;

// --lib が host に出す構造型 id (TCS_TYPE_*)。host は `object` で受けた値の
// 種別を GC header の type_id で見分け、同じ id を付けて作った値は C# 側の
// cast (tcs_cast の type_id 検査) を通る
public class LibTypeIdTests
{
    [CFact]
    public void LibMode_StructuralTypeIds_MatchCollectionsBothWays()
    {
        const string source = """
            using System.Collections.Generic;
            public class Probe
            {
                public static object Floats;
                public static object Ints;
                public static object Array;
                public static object FromHostFloats;
                public static object FromHostInts;
                public static void Setup()
                {
                    Floats = new List<float> { 1.5f };
                    Ints = new List<int> { 2 };
                    Array = new float[] { 3 };
                }
                public static void Report()
                {
                    System.Console.WriteLine(((List<float>)FromHostFloats)[0]);
                    System.Console.WriteLine(((List<int>)FromHostInts).Count);
                }
            }
            """;
        var c = Backends.EmitC([source], null, lib: true);
        // 同じ translation unit に置いて生成 C の内部 ABI (static / TcsList) を直接使う
        const string host = """

            static void check(const char *name, void *value, uint32_t id)
            {
                printf("%s:%d\n", name, TCS_GC_HEADER(value)->type_id == id);
            }
            int main(void)
            {
                float f = 2.5f;
                int32_t i = 7;
                TcsList *floats;
                TcsList *ints;
                tcs_lib_init();
                tcs_entry_Probe_setup();
                check("list_f32", tcs_s_Probe_floats, TCS_TYPE_LIST_F32);
                check("list_i32", tcs_s_Probe_ints, TCS_TYPE_LIST_I32);
                check("array_f32", tcs_s_Probe_array, TCS_TYPE_ARRAY_F32);
                check("list_f32_not_array", tcs_s_Probe_array, TCS_TYPE_LIST_F32);
                floats = tcs_typed(tcs_list_new(sizeof(float), NULL), TCS_TYPE_LIST_F32);
                tcs_list_add(floats, &f, sizeof(f), NULL);
                ints = tcs_typed(tcs_list_new(sizeof(int32_t), NULL), TCS_TYPE_LIST_I32);
                tcs_list_add(ints, &i, sizeof(i), NULL);
                tcs_list_add(ints, &i, sizeof(i), NULL);
                tcs_s_Probe_from_host_floats = floats;
                tcs_s_Probe_from_host_ints = ints;
                tcs_entry_Probe_report();
                return 0;
            }
            """;
        const string expected =
            "list_f32:1\nlist_i32:1\narray_f32:1\nlist_f32_not_array:0\n" +
            "2.5\n2";
        Assert.Equal(expected, Backends.CompileAndRunC(c + host));
        Assert.Equal(expected, Backends.CompileAndRunC(c + host, stress: true));
    }
}
