using TinyCs;

namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    private static string CompareStringsOrNumbers(string left, string right,
        CType leftType, CType rightType, string op) =>
        leftType == CType.String && rightType == CType.String
            ? $"(tcs_string_compare({left}, {right}) {op} 0)" : $"({left} {op} {right})";

    private void EmitForeachString(IlForeachList loop)
    {
        var text = Temp("text");
        var length = Temp("length");
        var index = Temp("index");
        var variable = new Variable(Temp("character"), CType.String);
        Line("{");
        _indent++;
        Line($"TcsString *{text} = {RenderExpr(loop.Coll)};");
        Line($"int32_t {length} = tcs_string_length({text});");
        Line($"for (int32_t {index} = 0; {index} < {length}; {index}++) {{");
        _indent++;
        PushScope();
        AddVariable(loop.Var, variable);
        _continueTargets.Push(null);
        Line($"TcsString *{variable.CName} = tcs_string_new({text}->data + {index}, 1);");
        EmitStats(loop.Body.Stats);
        _continueTargets.Pop();
        PopScope();
        _indent--;
        Line("}");
        _indent--;
        Line("}");
    }

    private CType? StringResult(IlCall call)
    {
        if (call.Callee == "String.Join")
        {
            RequireArity(call.Callee, call.Args.Length, 2);
            RequireType(CType.String, TypeOf(call.Args[0]), call.Callee);
            RequireType(CType.Array(CType.String), TypeOf(call.Args[1]), call.Callee);
            return CType.String;
        }
        if (call.Callee is "String.StartsWith" or "String.Split")
        {
            RequireArity(call.Callee, call.Args.Length, 2);
            foreach (var arg in call.Args) RequireType(CType.String, TypeOf(arg), call.Callee);
            return call.Callee == "String.Split" ? CType.Array(CType.String) : CType.Bool;
        }
        if (call.Callee == "tonumber")
        {
            RequireArity(call.Callee, call.Args.Length, 1);
            RequireType(CType.String, TypeOf(call.Args[0]), call.Callee);
            return CType.F32;
        }
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
        if (call.Callee == "String.Join") return RenderOrderedCall("tcs_string_join", result,
            call.Args.Select(a => (TypeOf(a), RenderExpr(a))).ToList());
        if (call.Callee is "String.StartsWith" or "String.Split")
        {
            var arguments = call.Args.Select(a => (CType.String, RenderExpr(a))).ToList();
            if (call.Callee == "String.Split") arguments.Insert(0, (CType.I32, RuntimeTypeId(result)));
            return RenderOrderedCall(call.Callee == "String.Split" ? "tcs_string_split" : "tcs_string_starts_with",
                result, arguments);
        }
        if (call.Callee == "tonumber") return RenderOrderedCall("tcs_parse_f32", result,
            [(CType.String, RenderExpr(call.Args[0]))]);
        var args = call.Args.Select(a => (TypeOf(a), RenderExpr(a))).ToList();
        var search = call.Callee == "String.IndexOf";
        if (args.Count == 2) args.Add((CType.I32, "0"));
        if (!search) args.Add((CType.Bool, call.Args.Length == 3 ? "true" : "false"));
        return RenderOrderedCall(search ? "tcs_string_index_of" : "tcs_string_substring",
            result, args);
    }

    private const string StringRuntime = """
        static TcsString *tcs_string_join(TcsString *separator, TcsArray *values)
        {
            size_t width = (size_t)tcs_string_length(separator), length = 0;
            int32_t count = tcs_array_length(values);
            TcsString **parts = values->data;
            for (int32_t i = 0; i < count; i++) {
                size_t part = (size_t)tcs_string_length(parts[i]);
                if (part > SIZE_MAX - length) tcs_fault("allocation-overflow");
                length += part;
                if (i > 0) {
                    if (width > SIZE_MAX - length) tcs_fault("allocation-overflow");
                    length += width;
                }
            }
            TcsString *result = tcs_string_new(NULL, length);
            size_t position = 0;
            for (int32_t i = 0; i < count; i++) {
                if (i > 0) { memcpy(result->data + position, separator->data, width); position += width; }
                memcpy(result->data + position, parts[i]->data, parts[i]->length);
                position += parts[i]->length;
            }
            return result;
        }

        static bool tcs_string_starts_with(TcsString *text, TcsString *prefix)
        {
            int32_t length = tcs_string_length(text), count = tcs_string_length(prefix);
            return count <= length && memcmp(text->data, prefix->data, (size_t)count) == 0;
        }

        static TcsArray *tcs_string_split(uint32_t type_id, TcsString *text, TcsString *separator)
        {
            int32_t length = tcs_string_length(text), width = tcs_string_length(separator), count = 1;
            if (width > 0)
                for (int32_t i = 0; i <= length - width; i++)
                    if (memcmp(text->data + i, separator->data, (size_t)width) == 0) {
                        if (count == INT32_MAX) tcs_fault("allocation-overflow");
                        count++; i += width - 1;
                    }
            TcsArray *result = tcs_array_new(type_id, count, sizeof(TcsString *), tcs_trace_ref);
            TcsString **parts = result->data;
            int32_t start = 0, part = 0;
            if (width > 0)
                for (int32_t i = 0; i <= length - width; i++)
                    if (memcmp(text->data + i, separator->data, (size_t)width) == 0) {
                        parts[part++] = tcs_string_new(text->data + start, (size_t)(i - start));
                        i += width - 1; start = i + 1;
                    }
            parts[part] = tcs_string_new(text->data + start, (size_t)(length - start));
            return result;
        }

        static float tcs_parse_f32(TcsString *text)
        {
            size_t length = (size_t)tcs_string_length(text);
            if (length == SIZE_MAX) tcs_fault("allocation-overflow");
            char *buffer = malloc(length + 1);
            if (buffer == NULL) tcs_fault("out-of-memory");
            memcpy(buffer, text->data, length);
            buffer[length] = 0;
            char *end;
            float result = strtof(buffer, &end);
            bool valid = end != buffer;
            while (end < buffer + length && (*end == ' ' || (*end >= '\t' && *end <= '\r'))) end++;
            valid = valid && end == buffer + length;
            free(buffer);
            if (!valid) tcs_fault("invalid-number");
            return result;
        }

        static int32_t tcs_string_byte(TcsString *text)
        {
            if (tcs_string_length(text) != 1) tcs_fault("invalid-character");
            return (unsigned char)text->data[0];
        }

        static int tcs_string_compare(TcsString *a, TcsString *b)
        {
            size_t an = (size_t)tcs_string_length(a), bn = (size_t)tcs_string_length(b);
            int result = memcmp(a->data, b->data, an < bn ? an : bn);
            return result != 0 ? result : (an > bn) - (an < bn);
        }

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
