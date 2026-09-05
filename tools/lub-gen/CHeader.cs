using System.Text;
using TinyCs;

namespace LubGen;

/// <summary>stub から C API の header (include/lub/lub_api.h) を導く。規則は
/// docs/log/2026-09-05-language-architecture-plan.md の「段階 4 で決めたこと」。
/// C の関数は stub の関数と 1 対 1、struct は class と 1 対 1。</summary>
public static class CHeader
{
    private const string Prologue = """
        // lub の C API。cs-lib/lub_stub.cs から tools/lub-gen が生成する (手で
        // 編集しない。再生成: dotnet run --project tools/lub-gen -- header)。
        //
        // 規則 (docs/log/2026-09-05-language-architecture-plan.md 段階 4):
        //   - context を第 1 引数に取り、失敗しうる関数は LubStatus を返す。
        //     失敗は LUB_ERROR と lub_last_error() の文字列、問い合わせの対象が
        //     無いときは LUB_NOT_FOUND (last_error は書かない)。
        //   - 型は int32 / float / bool / UTF-8 の byte 列だけ。文字列は LubStr
        //     (pointer + length、NUL 終端を要求しない)。
        //   - ゲームの memory は呼び出しの間だけ借用する。runtime の memory は
        //     LubView か runtime 所有の配列 (frame の終わりまで有効) で返す。
        //     frame を跨いで生きるものは runtime 所有の keyed resource で、
        //     ゲームは key と int32 の handle だけ持つ。
        //   - 省略可能な field は has_x + x (実装が既定値を入れる)。省略可能な
        //     引数は pointer (NULL = 無し)。
        //   - main thread 限定。
        #pragma once
        #include <stdbool.h>
        #include <stdint.h>

        #ifdef __cplusplus
        extern "C" {
        #endif

        typedef struct LubContext LubContext;

        typedef enum LubStatus {
          LUB_OK = 0,
          LUB_ERROR = 1,
          LUB_NOT_FOUND = 2,
        } LubStatus;

        // UTF-8 の byte 列。ptr は len byte だけ有効で NUL 終端は要らない。
        typedef struct LubStr {
          const char *ptr;
          int32_t len;
        } LubStr;

        // runtime の memory への view。frame 番号 `frame` の間だけ有効。
        typedef struct LubView {
          const uint8_t *ptr;
          int32_t len;
          int32_t frame;
        } LubView;

        // runtime 所有の resource への handle。0 = 無し。resource が sweep されるまで
        // 同じ値で、hot reload を跨いでも有効。
        typedef int32_t LubHandle;

        // draw / dispatch の bindings の 1 項目。name は shader の reflection 名。
        // handle が 0 でなければ buffer / texture の束縛、そうでなければ values
        // (count 個の float) の uniform 値。
        typedef struct LubBinding {
          LubStr name;
          LubHandle handle;
          const float *values;
          int32_t count;
        } LubBinding;

        // 直近の LUB_ERROR の message。次の API 呼び出しまで有効。
        const char *lub_last_error(LubContext *ctx);

        // 現在の frame 番号 (LubView.frame と比較する)。
        int32_t lub_frame_index(LubContext *ctx);

        """;

    private const string Epilogue = """

        #ifdef __cplusplus
        }
        #endif

        """;

    public static string Generate(ApiModel model)
    {
        var g = new Gen(model);
        return g.Run();
    }

    private sealed class Gen(ApiModel model)
    {
        private readonly StringBuilder sb = new();

        public string Run()
        {
            sb.Append(Prologue);
            EmitEnums();
            EmitConsts();
            EmitTypes();
            foreach (var ns in model.Namespaces)
                EmitNamespaceFunctions(ns);
            sb.Append(Epilogue);
            return sb.ToString();
        }

        // ------------------------------------------------------------ names

        public static string TypeName(string csName) => "Lub" + csName;

        public static string EnumName(string qualified)
        {
            var parts = qualified.Split('.');
            return "Lub" + string.Concat(parts);
        }

        public static string EnumMember(ApiEnum e, ApiEnumMember m) =>
            $"LUB_{LuaNaming.Const(e.Namespace)}_{LuaNaming.Const(e.Name)}_{m.LuaName}";

        public static string FunctionName(ApiNamespace ns, string luaName)
        {
            var prefix = ns.LuaPath == "lub" ? "lub" : "lub_" + ns.LuaPath["lub.".Length..].Replace('.', '_');
            return prefix + "_" + luaName;
        }

        private static string FnTypedefName(ApiNamespace ns, ApiFunction f, ApiParam p) =>
            "Lub" + ns.Name + f.Name + char.ToUpperInvariant(p.Name[0]) + p.Name[1..] + "Fn";

        // ------------------------------------------------------------ enums

        private void EmitEnums()
        {
            foreach (var ns in model.Namespaces)
                foreach (var e in ns.Enums)
                {
                    Comment(e.Doc);
                    if (e.LuaString)
                        Comment("Lua 面では小文字の文字列 (\"" + e.Members[0].LuaName.ToLowerInvariant() + "\" 等)。");
                    sb.Append($"typedef enum {EnumName(ns.Name + "." + e.Name)} {{\n");
                    foreach (var m in e.Members)
                    {
                        sb.Append($"  {EnumMember(e, m)} = {m.Value},");
                        if (m.Doc.Length > 0) sb.Append(" // " + m.Doc);
                        sb.Append('\n');
                    }
                    sb.Append($"}} {EnumName(ns.Name + "." + e.Name)};\n\n");
                }
        }

        private void EmitConsts()
        {
            foreach (var ns in model.Namespaces)
                foreach (var c in ns.Consts)
                {
                    if (c.Value is not int) continue;
                    Comment(c.Doc);
                    sb.Append($"#define LUB_{LuaNaming.Const(ns.Name)}_{c.LuaName} {c.Value}\n\n");
                }
        }

        // ------------------------------------------------------------ types

        private void EmitTypes()
        {
            var records = model.Types.Where(t => t.Kind == "record").ToList();
            var byName = records.ToDictionary(t => t.Name);
            var done = new HashSet<string>(StringComparer.Ordinal);
            var visiting = new HashSet<string>(StringComparer.Ordinal);

            void Visit(ApiType t)
            {
                if (done.Contains(t.Name)) return;
                if (!visiting.Add(t.Name))
                    throw new InvalidOperationException($"type cycle at {t.Name}");
                if (t.Base != null && byName.TryGetValue(t.Base, out var b)) Visit(b);
                foreach (var dep in Deps(t))
                    if (byName.TryGetValue(dep, out var d)) Visit(d);
                visiting.Remove(t.Name);
                done.Add(t.Name);
                EmitStruct(t);
            }

            foreach (var t in records) Visit(t);
        }

        private static IEnumerable<string> Deps(ApiType t)
        {
            foreach (var f in t.Fields)
                foreach (var d in Deps(f.Type))
                    yield return d;
        }

        private static IEnumerable<string> Deps(TypeRef tr)
        {
            switch (tr.Kind)
            {
                case LubTypeKind.Record:
                    yield return tr.Name;
                    break;
                case LubTypeKind.List:
                case LubTypeKind.Array:
                    foreach (var d in Deps(tr.Elem!)) yield return d;
                    break;
                case LubTypeKind.Func:
                    foreach (var p in tr.FuncParams!)
                        foreach (var d in Deps(p)) yield return d;
                    break;
            }
        }

        private void EmitStruct(ApiType t)
        {
            var name = TypeName(t.Name);
            Comment(t.Doc);
            sb.Append($"typedef struct {name} {{\n");
            var any = false;
            if (t.Base != null)
            {
                sb.Append($"  {TypeName(t.Base)} base;\n");
                any = true;
            }
            if (t.Fields.Any(f => f.Type.Kind == LubTypeKind.Func))
            {
                sb.Append("  void *user; // callback に渡す\n");
                sb.Append("  // runtime が callback を手放すとき (次の宣言で置き換える、resource が\n");
                sb.Append("  // sweep される) に呼ぶ。NULL 可。\n");
                sb.Append("  void (*user_release)(void *user);\n");
                any = true;
            }
            foreach (var f in t.Fields)
            {
                EmitField(f);
                any = true;
            }
            if (!any) sb.Append("  int32_t unused; // 空の struct を避ける\n");
            sb.Append($"}} {name};\n\n");
        }

        private void EmitField(ApiField f)
        {
            var n = f.LuaName;
            var tr = f.Type;
            var doc = f.Doc.Length > 0 ? " // " + f.Doc : "";
            string line;
            switch (tr.Kind)
            {
                case LubTypeKind.Int:
                case LubTypeKind.Double:
                case LubTypeKind.Bool:
                    if (f.Optional) sb.Append($"  bool has_{n};\n");
                    line = $"  {Scalar(tr)} {n};";
                    break;
                case LubTypeKind.Enum:
                    if (f.Optional) sb.Append($"  bool has_{n};\n");
                    line = $"  int32_t {n}; // {EnumName(tr.Name)}";
                    if (f.Doc.Length > 0) line += "。" + f.Doc;
                    doc = "";
                    break;
                case LubTypeKind.String:
                    if (f.Bits)
                    {
                        if (f.Optional) sb.Append($"  bool has_{n};\n");
                        line = $"  uint64_t {n}; // bit mask (Lua 面は hex 文字列)";
                        if (f.Doc.Length > 0) line += "。" + f.Doc;
                        doc = "";
                    }
                    else
                        line = $"  LubStr {n};" + (f.Optional ? " // len 0 = 無し" : "");
                    break;
                case LubTypeKind.Handle:
                    line = $"  LubHandle {n};" + (f.Optional ? " // 0 = 無し" : "");
                    break;
                case LubTypeKind.Keyed:
                    line = $"  LubStr {n};";
                    break;
                case LubTypeKind.View:
                    line = $"  LubView {n};";
                    break;
                case LubTypeKind.Record:
                    if (f.Optional) sb.Append($"  bool has_{n};\n");
                    line = $"  {TypeName(tr.Name)} {n};";
                    break;
                case LubTypeKind.List:
                    {
                        var elem = tr.Elem!;
                        if (elem.Kind == LubTypeKind.Array)
                        {
                            var len = f.ArrayLen ?? throw new InvalidOperationException($"{f.Name}: List of array needs [LubArray]");
                            line = $"  const {Scalar(elem.Elem!)} (*{n})[{len}];\n  int32_t {n}_count;";
                        }
                        else if (f.ArrayLen is int len)
                        {
                            line = $"  {ElemType(elem)} {n}[{len}];\n  int32_t {n}_count;";
                        }
                        else
                        {
                            line = $"  const {ElemType(elem)} *{n};" + (f.Optional ? " // NULL = 無し" : "") + $"\n  int32_t {n}_count;";
                        }
                        break;
                    }
                case LubTypeKind.Array:
                    {
                        var len = f.ArrayLen ?? throw new InvalidOperationException($"{f.Name}: array field needs [LubArray]");
                        if (f.Optional) sb.Append($"  bool has_{n};\n");
                        line = $"  {Scalar(tr.Elem!)} {n}[{len}];";
                        break;
                    }
                case LubTypeKind.Func:
                    line = $"  {FuncPointer(tr, n)};";
                    break;
                default:
                    throw new InvalidOperationException($"{f.Name}: unsupported field type {tr}");
            }
            sb.Append(line).Append(doc).Append('\n');
        }

        private static string Scalar(TypeRef tr) => tr.Kind switch
        {
            LubTypeKind.Int => "int32_t",
            LubTypeKind.Double => "float",
            LubTypeKind.Bool => "bool",
            LubTypeKind.Enum => "int32_t",
            _ => throw new InvalidOperationException($"not a scalar: {tr}"),
        };

        private static string ElemType(TypeRef elem) => elem.Kind switch
        {
            LubTypeKind.Int or LubTypeKind.Double or LubTypeKind.Bool or LubTypeKind.Enum => Scalar(elem),
            LubTypeKind.String => "LubStr",
            LubTypeKind.Handle => "LubHandle",
            LubTypeKind.Record => TypeName(elem.Name),
            _ => throw new InvalidOperationException($"unsupported element type {elem}"),
        };

        private static string FuncReturn(TypeRef ret) => ret.Kind switch
        {
            LubTypeKind.Void => "void",
            LubTypeKind.Bool => "bool",
            LubTypeKind.Double => "float",
            LubTypeKind.Int => "int32_t",
            _ => throw new InvalidOperationException($"unsupported callback return {ret}"),
        };

        private static string FuncParams(TypeRef fn)
        {
            var parts = new List<string> { "void *user" };
            var i = 0;
            foreach (var p in fn.FuncParams!)
            {
                var pn = (char)('a' + i);
                parts.Add(p.Kind switch
                {
                    LubTypeKind.Record => $"const {TypeName(p.Name)} *{pn}",
                    LubTypeKind.Int or LubTypeKind.Double or LubTypeKind.Bool or LubTypeKind.Enum => $"{Scalar(p)} {pn}",
                    _ => throw new InvalidOperationException($"unsupported callback param {p}"),
                });
                i++;
            }
            return string.Join(", ", parts);
        }

        private static string FuncPointer(TypeRef fn, string name) =>
            $"{FuncReturn(fn.FuncReturn!)} (*{name})({FuncParams(fn)})";

        // -------------------------------------------------------- functions

        private void EmitNamespaceFunctions(ApiNamespace ns)
        {
            var label = ns.LuaPath == "lub" ? "core" : ns.LuaPath["lub.".Length..];
            sb.Append("// ").Append(new string('-', Math.Max(1, 70 - label.Length))).Append(' ').Append(label).Append('\n');
            if (ns.Doc.Length > 0) Comment(ns.Doc);
            sb.Append('\n');
            foreach (var f in ns.StaticFields)
            {
                Comment(f.Doc);
                sb.Append($"{ReturnCType(f.Type)} {FunctionName(ns, f.LuaName)}(LubContext *ctx);\n\n");
            }
            foreach (var f in ns.Functions)
            {
                if (f.NoC) continue;
                foreach (var p in f.Params.Where(p => p.Type.Kind == LubTypeKind.Func))
                {
                    var fn = p.Type;
                    sb.Append($"typedef {FuncReturn(fn.FuncReturn!)} (*{FnTypedefName(ns, f, p)})({FuncParams(fn)});\n");
                }
                Comment(f.Doc);
                sb.Append(Prototype(ns, f)).Append("\n\n");
            }
        }

        private static string ReturnCType(TypeRef tr) => tr.Kind switch
        {
            LubTypeKind.Void => "void",
            LubTypeKind.Int => "int32_t",
            LubTypeKind.Double => "float",
            LubTypeKind.Bool => "bool",
            LubTypeKind.Enum => "int32_t",
            LubTypeKind.Handle => "LubHandle",
            _ => throw new InvalidOperationException($"unsupported direct return {tr}"),
        };

        private string Prototype(ApiNamespace ns, ApiFunction f)
        {
            var args = new List<string> { "LubContext *ctx" };
            foreach (var p in f.Params.Where(p => !p.IsOut))
                args.AddRange(InParam(ns, f, p));
            foreach (var p in f.Params.Where(p => p.IsOut))
            {
                args.AddRange(OutParam(p.Type, p.LuaName, p.ArrayLen));
                if (p.Type.Kind == LubTypeKind.Record && p.Type.Nullable)
                    args.Add($"bool *has_{p.LuaName}");
            }
            string ret;
            var r = f.Return;
            if (f.NoFail)
            {
                if (r.Kind is LubTypeKind.Void or LubTypeKind.Int or LubTypeKind.Double or LubTypeKind.Bool or LubTypeKind.Enum or LubTypeKind.Handle)
                    ret = ReturnCType(r);
                else
                {
                    ret = "void";
                    args.AddRange(OutParam(r, "out", null));
                }
            }
            else
            {
                ret = "LubStatus";
                if (r.Kind != LubTypeKind.Void)
                {
                    args.AddRange(OutParam(r, "out", null));
                    if (r.Nullable && (r.IsScalar || (r.Kind == LubTypeKind.Record && f.Maybe)))
                        args.Add("bool *has");
                }
            }
            return Wrap($"{ret} {FunctionName(ns, f.LuaName)}(", string.Join(", ", args) + ");");
        }

        private static IEnumerable<string> InParam(ApiNamespace ns, ApiFunction f, ApiParam p)
        {
            var n = p.LuaName;
            var tr = p.Type;
            switch (tr.Kind)
            {
                case LubTypeKind.Int:
                case LubTypeKind.Double:
                case LubTypeKind.Bool:
                case LubTypeKind.Enum:
                    yield return tr.Nullable ? $"const {Scalar(tr)} *{n}" : $"{Scalar(tr)} {n}";
                    break;
                case LubTypeKind.String:
                    yield return $"LubStr {n}";
                    break;
                case LubTypeKind.Handle:
                    yield return $"LubHandle {n}";
                    break;
                case LubTypeKind.Keyed:
                    yield return $"LubStr {n}";
                    break;
                case LubTypeKind.View:
                    yield return $"const uint8_t *{n}";
                    yield return $"int32_t {n}_len";
                    break;
                case LubTypeKind.Record:
                    yield return $"const {TypeName(tr.Name)} *{n}";
                    break;
                case LubTypeKind.List:
                    yield return $"const {ElemType(tr.Elem!)} *{n}";
                    yield return $"int32_t {n}_count";
                    break;
                case LubTypeKind.Func:
                    yield return $"{FnTypedefName(ns, f, p)} {n}";
                    yield return $"void *{n}_user";
                    break;
                case LubTypeKind.Dict:
                    yield return $"const LubBinding *{n}";
                    yield return $"int32_t {n}_count";
                    break;
                default:
                    throw new InvalidOperationException($"{ns.Name}.{f.Name}: unsupported parameter {p.Name}: {tr}");
            }
        }

        private static IEnumerable<string> OutParam(TypeRef tr, string n, int? arrayLen)
        {
            switch (tr.Kind)
            {
                case LubTypeKind.Int:
                case LubTypeKind.Double:
                case LubTypeKind.Bool:
                case LubTypeKind.Enum:
                    yield return $"{Scalar(tr)} *{n}";
                    break;
                case LubTypeKind.String:
                    yield return $"LubStr *{n}";
                    break;
                case LubTypeKind.Handle:
                    yield return $"LubHandle *{n}";
                    break;
                case LubTypeKind.View:
                    yield return $"LubView *{n}";
                    break;
                case LubTypeKind.Record:
                    yield return $"{TypeName(tr.Name)} *{n}";
                    break;
                case LubTypeKind.List:
                    yield return $"const {ElemType(tr.Elem!)} **{n}";
                    yield return $"int32_t *{n}_count";
                    break;
                default:
                    throw new InvalidOperationException($"unsupported out / return type {tr}");
            }
        }

        // ---------------------------------------------------------- helpers

        private void Comment(string doc)
        {
            if (doc.Length == 0) return;
            foreach (var line in WrapText(doc, 76))
                sb.Append("// ").Append(line).Append('\n');
        }

        // 表示幅 (CJK は 2) で折り返す。折り返し位置は空白の後か、CJK の文字
        // (句読点・閉じ括弧を除く) の前。空白は原文のものだけ。
        private static IEnumerable<string> WrapText(string text, int width)
        {
            var start = 0;
            while (start < text.Length)
            {
                var w = 0;
                var lastBreak = -1;
                var i = start;
                for (; i < text.Length; i++)
                {
                    var c = text[i];
                    var cw = IsWide(c) ? 2 : 1;
                    if (w + cw > width && lastBreak > start) break;
                    if (c == ' ') lastBreak = i + 1;
                    else if (IsWide(c) && !IsWidePunct(c) && i > start && text[i - 1] != ' ') lastBreak = i;
                    w += cw;
                }
                var end = i >= text.Length ? text.Length : lastBreak;
                yield return text[start..end].Trim();
                start = end;
                while (start < text.Length && text[start] == ' ') start++;
            }
        }

        private static bool IsWide(char c) => c > 0x2E7F && !char.IsSurrogate(c);

        private static bool IsWidePunct(char c) => "。、」』）］〕〉》・ー：；！？".Contains(c);

        // clang-format 風に 80 桁で引数を折り返す。
        private static string Wrap(string head, string rest)
        {
            var indent = new string(' ', head.Length);
            var parts = rest.Split(", ");
            var sb = new StringBuilder(head);
            var col = head.Length;
            for (var i = 0; i < parts.Length; i++)
            {
                var piece = parts[i] + (i + 1 < parts.Length ? "," : "");
                if (col + piece.Length + (i > 0 ? 1 : 0) > 80 && i > 0)
                {
                    sb.Append('\n').Append(indent);
                    col = indent.Length;
                }
                else if (i > 0)
                {
                    sb.Append(' ');
                    col++;
                }
                sb.Append(piece);
                col += piece.Length;
            }
            return sb.ToString();
        }
    }
}
