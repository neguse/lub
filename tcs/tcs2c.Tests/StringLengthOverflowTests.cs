namespace TinyCs.Tcs2c.Tests;

// 文字列の長さの合計は size_t に収まらなければ確保の前に fault する。
// 実際に作れる大きさでは 32bit の size_t でしか起きないので、長さだけを持つ TcsString を runtime の関数へ直接渡す
public class StringLengthOverflowTests
{
    private const string Harness = """
        static TcsString s_huge = { SIZE_MAX - 1 };
        static TcsString s_two = { 2 };
        static TcsString s_one = { 1 };
        static TcsString s_empty = { 0 };
        int main(void)
        {
            tcs_lib_init();
            BODY
            puts("not faulted");
            return 0;
        }
        """;

    [CTheory]
    [InlineData("string-length-overflow", "TcsString *items[] = { &s_huge, &s_two }; tcs_string_join_items(&s_empty, items, 2);")]
    [InlineData("string-length-overflow", "TcsString *items[] = { &s_huge, &s_empty }; tcs_string_join_items(&s_two, items, 2);")]
    [InlineData("string-length-overflow", "TcsString *names[] = { &s_two, &s_huge }; int32_t values[] = { 1, 2 }; tcs_enum_format(3, names, values, 2, true);")]
    [InlineData("string-length-overflow", "TcsString *names[] = { &s_one, &s_huge }; int32_t values[] = { 1, 2 }; tcs_enum_format(3, names, values, 2, true);")]
    [InlineData("allocation-overflow", "int32_t data[1] = { 0 }; tcs_string_join_values(&s_empty, data, SIZE_MAX / sizeof(TcsString *) + 2, TCS_JOIN_I32);")]
    [InlineData("allocation-overflow", "int32_t data[1] = { 0 }; TcsString *names[] = { &s_one }; int32_t values[] = { 0 }; tcs_string_join_enum(&s_empty, data, SIZE_MAX / sizeof(TcsString *) + 2, names, values, 1, false);")]
    public void LengthThatDoesNotFit_FaultsBeforeWriting(string fault, string body)
    {
        var c = Backends.EmitC(["public class P { public static void Frame() { } }"], null, lib: true)
            + Harness.Replace("BODY", body);
        var error = Assert.Throws<InvalidOperationException>(() => Backends.CompileAndRunC(c));
        Assert.Contains("TinyC# fault: " + fault, error.Message);
    }
}
