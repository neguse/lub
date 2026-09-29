using TinyCs;

namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    private CType? StringResult(IlCall call)
    {
        if (call.Callee is not ("String.IndexOf" or "String.Substring")) return null;
        if (call.Args.Length is not (2 or 3))
            throw new Tcs2cException($"{call.Callee}: expected 2 or 3 arguments");
        RequireType(CType.String, TypeOf(call.Args[0]), call.Callee);
        RequireType(call.Callee == "String.IndexOf" ? CType.String : CType.I32,
            TypeOf(call.Args[1]), call.Callee);
        if (call.Args.Length == 3)
            RequireType(CType.I32, TypeOf(call.Args[2]), call.Callee);
        return call.Callee == "String.IndexOf" ? CType.I32 : CType.String;
    }

    private string RenderStringCall(IlCall call, CType result)
    {
        var args = call.Args.Select(a => (TypeOf(a), RenderExpr(a))).ToList();
        var search = call.Callee == "String.IndexOf";
        if (args.Count == 2) args.Add((CType.I32, "0"));
        if (!search) args.Add((CType.Bool, call.Args.Length == 3 ? "true" : "false"));
        return RenderOrderedCall(search ? "tcs_string_index_of" : "tcs_string_substring",
            result, args);
    }

    private const string StringRuntime = """
        static int32_t tcs_string_index_of(TcsString *text, TcsString *value, int32_t start)
        {
            int64_t length = tcs_string_length(text);
            int64_t count = tcs_string_length(value);
            int64_t first = (int64_t)start + 1;
            if (first < 0) first = length + first + 1;
            if (first < 1) first = 1;
            for (int64_t i = first - 1; i <= length - count; i++)
                if (count == 0 || memcmp(text->data + i, value->data, (size_t)count) == 0)
                    return (int32_t)i;
            return -1;
        }

        static TcsString *tcs_string_substring(TcsString *text, int32_t start,
            int32_t count, bool has_count)
        {
            int64_t length = tcs_string_length(text);
            int64_t first = (int64_t)start + 1;
            int64_t last = has_count ? (int64_t)start + count : length;
            if (first < 0) first = length + first + 1;
            if (last < 0) last = length + last + 1;
            if (first < 1) first = 1;
            if (last > length) last = length;
            if (first > last) return tcs_string_new(NULL, 0);
            return tcs_string_new(text->data + first - 1, (size_t)(last - first + 1));
        }

        """;
}
