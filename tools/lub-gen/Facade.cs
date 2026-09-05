using System.Text;
using TinyCs;

namespace LubGen;

/// <summary>stub から .NET 実行の facade (dotnet/Lub/Lub.g.cs) を導く。
/// stub と同じ C# の面 (class / enum / static メソッド) を持ち、中身は
/// C API (CHeader が導いた形) への詰め替えと P/Invoke。Lua binding
/// (LuaBinding) と同じ規則を C# で書いたもので、土台 (arena、例外、
/// callback の箱、host) は手書きの dotnet/Lub/LubRuntime.cs にある。</summary>
public static class Facade
{
    public static string Generate(ApiModel model) => new Gen(model).Run();

    private sealed class Gen(ApiModel model)
    {
        private readonly StringBuilder sb = new();
        private readonly Dictionary<string, ApiType> types = model.Types.ToDictionary(t => t.Name);
        private readonly Dictionary<string, ApiEnum> enums =
            model.Namespaces.SelectMany(n => n.Enums.Select(e => (n.Name + "." + e.Name, e)))
                .ToDictionary(p => p.Item1, p => p.e);

        public string Run()
        {
            sb.Append("""
                // lub の .NET 実行の facade。cs-lib/lub_stub.cs から tools/lub-gen が生成する
                // (手で編集しない。再生成: scripts/gen-api.sh)。stub と同じ面を持ち、中身は
                // C API (include/lub/lub_api.h) への詰め替えと P/Invoke。土台は LubRuntime.cs。
                #nullable enable
                #pragma warning disable CS0649, CS8618, CS0169, CS0414, IDE1006
                using System;
                using System.Collections.Generic;
                using System.Runtime.CompilerServices;
                using System.Runtime.InteropServices;


                """);
            foreach (var t in model.Types.Where(t => t.Kind != "record")) EmitOpaque(t);
            foreach (var t in model.Types.Where(t => t.Kind == "record")) EmitRecord(t);
            EmitApi();
            EmitNative();
            return sb.ToString();
        }

        // ------------------------------------------------------------ names

        private static string C(string csName) => "Lub" + csName;

        private static string NativeFn(ApiNamespace ns, string luaName) => CHeader.FunctionName(ns, luaName);

        private string EnumCs(TypeRef tr)
        {
            // stub の enum は namespace class の入れ子: Gfx.LoadAction / Lub.EventKind
            var e = enums[tr.Name];
            return e.Namespace == ApiModelLoader.RootClass ? "Lub." + e.Name : "Lub." + tr.Name;
        }

        // 公開面の型 (stub と同じ表記)。root の enum は Lub. 付き。
        private string Cs(TypeRef tr)
        {
            if (tr.Kind == LubTypeKind.Enum) return EnumCs(tr) + (tr.Nullable ? "?" : "");
            if (tr.Kind == LubTypeKind.List) return $"List<{Cs(tr.Elem!)}>" + (tr.Nullable ? "?" : "");
            if (tr.Kind == LubTypeKind.Array) return $"{Cs(tr.Elem!)}[]" + (tr.Nullable ? "?" : "");
            if (tr.Kind == LubTypeKind.Func)
            {
                var ps = tr.FuncParams!.Select(Cs).ToList();
                var r = tr.FuncReturn!;
                var s = r.Kind == LubTypeKind.Void
                    ? (ps.Count == 0 ? "Action" : $"Action<{string.Join(", ", ps)}>")
                    : $"Func<{string.Join(", ", ps.Append(Cs(r)))}>";
                return s + (tr.Nullable ? "?" : "");
            }
            return CsNames.Type(tr);
        }

        // native 側の struct field / 引数の型
        private static string NScalar(TypeRef tr) => tr.Kind switch
        {
            LubTypeKind.Int or LubTypeKind.Enum => "int",
            LubTypeKind.Double => "float",
            LubTypeKind.Bool => "bool",
            _ => throw new InvalidOperationException($"not a scalar: {tr}"),
        };

        private static string NElem(TypeRef elem) => elem.Kind switch
        {
            LubTypeKind.Int or LubTypeKind.Double or LubTypeKind.Bool or LubTypeKind.Enum => NScalar(elem),
            LubTypeKind.String => "LubStr",
            LubTypeKind.Handle => "int",
            LubTypeKind.Record => C(elem.Name),
            _ => throw new InvalidOperationException($"unsupported element type {elem}"),
        };

        private static string FnPtr(TypeRef fn)
        {
            var ps = new List<string> { "void*" };
            foreach (var p in fn.FuncParams!)
                ps.Add(p.Kind switch
                {
                    LubTypeKind.Record => C(p.Name) + "*",
                    LubTypeKind.Int or LubTypeKind.Enum => "int",
                    LubTypeKind.Double => "float",
                    LubTypeKind.Bool => "byte",
                    _ => throw new InvalidOperationException($"unsupported callback param {p}"),
                });
            ps.Add(fn.FuncReturn!.Kind switch
            {
                LubTypeKind.Void => "void",
                LubTypeKind.Bool => "byte",
                LubTypeKind.Double => "float",
                LubTypeKind.Int => "int",
                _ => throw new InvalidOperationException($"unsupported callback return {fn.FuncReturn}"),
            });
            return $"delegate* unmanaged[Cdecl]<{string.Join(", ", ps)}>";
        }

        private IEnumerable<ApiField> AllFields(ApiType t)
        {
            if (t.Base != null && types.TryGetValue(t.Base, out var b))
                foreach (var f in AllFields(b)) yield return f;
            foreach (var f in t.Fields) yield return f;
        }

        private bool HasFuncs(ApiType t) => t.Fields.Any(f => f.Type.Kind == LubTypeKind.Func);

        private static string Doc(string doc) => doc.Length > 0 ? $"    /// <summary>{doc}</summary>\n" : "";

        // ------------------------------------------------------ opaque types

        private void EmitOpaque(ApiType t)
        {
            sb.Append(Doc(t.Doc).Replace("    ///", "///"));
            switch (t.Kind)
            {
                case "handle":
                    sb.Append($"public sealed class {t.Name}\n{{\n");
                    sb.Append("    internal readonly int H;\n");
                    sb.Append($"    internal {t.Name}(int h) {{ H = h; }}\n");
                    foreach (var f in t.Fields)
                    {
                        if (f.Name == "Version" && f.Type.Kind == LubTypeKind.Int)
                            sb.Append("    public int Version => LubRuntime.ResourceVersion(H);\n");
                        else
                            throw new InvalidOperationException($"{t.Name}.{f.Name}: handle field is not supported by the facade");
                    }
                    sb.Append("}\n\n");
                    break;
                case "view":
                    sb.Append($"public sealed unsafe class {t.Name}\n{{\n");
                    sb.Append("    internal readonly byte* Ptr;\n    internal readonly int Frame;\n");
                    sb.Append("    public int Length;\n");
                    sb.Append($"    internal {t.Name}(byte* ptr, int len, int frame) {{ Ptr = ptr; Length = len; Frame = frame; }}\n");
                    sb.Append("    /// <summary>この frame の間だけ有効な view。</summary>\n");
                    sb.Append("    public ReadOnlySpan<byte> AsSpan() { LubRuntime.CheckView(Frame); return new ReadOnlySpan<byte>(Ptr, Length); }\n");
                    sb.Append("    public byte[] ToArray() => AsSpan().ToArray();\n");
                    foreach (var m in t.Methods)
                    {
                        if (m.Name == "Get" && m.Params.Count == 1)
                            sb.Append($"    public int {m.Name}(int {m.Params[0].Name}) => AsSpan()[{m.Params[0].Name}];\n");
                        else
                            throw new InvalidOperationException($"{t.Name}.{m.Name}: view method is not supported by the facade");
                    }
                    sb.Append("}\n\n");
                    break;
                case "keyed":
                    sb.Append($"public sealed class {t.Name}\n{{\n");
                    sb.Append("    public readonly string Key;\n");
                    sb.Append($"    public {t.Name}(string key) {{ Key = key; }}\n");
                    sb.Append("}\n\n");
                    break;
                default:
                    throw new InvalidOperationException($"{t.Name}: unknown kind {t.Kind}");
            }
        }

        // ---------------------------------------------------------- records

        private void EmitRecord(ApiType t)
        {
            sb.Append(Doc(t.Doc).Replace("    ///", "///"));
            sb.Append($"public class {t.Name}{(t.Base != null ? " : " + t.Base : "")}\n{{\n");
            foreach (var f in t.Fields)
            {
                sb.Append(Doc(f.Doc));
                var init = f.Type.Kind == LubTypeKind.List && !f.Type.Nullable ? $" = new {Cs(f.Type)}()" : "";
                sb.Append($"    public {Cs(f.Type)} {f.Name}{init};\n");
            }
            sb.Append("}\n\n");
        }

        // -------------------------------------------------------------- api

        private void EmitApi()
        {
            sb.Append("public static unsafe partial class Lub\n{\n");
            foreach (var ns in model.Namespaces)
            {
                var root = ns.Name == ApiModelLoader.RootClass;
                var ind = root ? "    " : "        ";
                if (!root)
                {
                    sb.Append(Doc(ns.Doc));
                    sb.Append($"    public static unsafe class {ns.Name}\n    {{\n");
                }
                foreach (var e in ns.Enums)
                {
                    if (e.Doc.Length > 0) sb.Append($"{ind}/// <summary>{e.Doc}</summary>\n");
                    sb.Append($"{ind}public enum {e.Name}\n{ind}{{\n");
                    foreach (var m in e.Members)
                        sb.Append($"{ind}    {m.Name} = {m.Value},\n");
                    sb.Append($"{ind}}}\n\n");
                }
                foreach (var c in ns.Consts)
                    sb.Append($"{ind}public const {CsNames.ConstType(c.Value)} {c.Name} = {CsNames.Const(c.Value)};\n");
                foreach (var f in ns.StaticFields)
                {
                    if (f.Doc.Length > 0) sb.Append($"{ind}/// <summary>{f.Doc}</summary>\n");
                    var call = $"LubNative.{NativeFn(ns, f.LuaName)}(LubRuntime.Ctx)";
                    sb.Append($"{ind}public static {Cs(f.Type)} {f.Name} => {FromDirect(f.Type, call)};\n\n");
                }
                foreach (var f in ns.Functions)
                    EmitFunction(ns, f, ind);
                if (!root) sb.Append("    }\n\n");
            }
            sb.Append("}\n\n");
        }

        private static string Ret(TypeRef r) => r.Kind == LubTypeKind.Void ? "void" : null!;

        // 直接値 (NoFail の戻り値、static field) を公開型に写す
        private string FromDirect(TypeRef tr, string expr) => tr.Kind switch
        {
            LubTypeKind.Void => expr,
            LubTypeKind.Int => expr,
            LubTypeKind.Double => expr,
            LubTypeKind.Bool => $"({expr} != 0)",
            LubTypeKind.Enum => $"({EnumCs(tr)}){expr}",
            LubTypeKind.Handle => $"LubNative.H_{tr.Name}({expr})",
            _ => throw new InvalidOperationException($"unsupported direct value {tr}"),
        };

        private void EmitFunction(ApiNamespace ns, ApiFunction f, string ind)
        {
            var ps = f.Params.Select(p => $"{(p.IsOut ? "out " : "")}{Cs(p.Type)} {p.Name}{(p.Optional ? " = null" : "")}");
            if (f.Doc.Length > 0) sb.Append($"{ind}/// <summary>{f.Doc}</summary>\n");
            sb.Append($"{ind}public static {Cs(f.Return)} {f.Name}({string.Join(", ", ps)})\n{ind}{{\n");
            var b = new StringBuilder();
            var i2 = ind + "    ";
            if (f.NoC)
            {
                if (f.Return.Kind == LubTypeKind.Keyed && f.Params.Count == 1 && f.Params[0].Type.Kind == LubTypeKind.String)
                    b.Append($"{i2}return new {f.Return.Name}({f.Params[0].Name});\n");
                else
                    throw new InvalidOperationException($"{ns.Name}.{f.Name}: [LubNoC] needs a hand-written facade");
                sb.Append(b).Append($"{ind}}}\n\n");
                return;
            }
            b.Append($"{i2}var a = LubRuntime.Arena.Begin();\n{i2}try\n{i2}{{\n");
            var i3 = i2 + "    ";
            var args = new List<string> { "LubRuntime.Ctx" };
            var post = new StringBuilder(); // 呼び出し後 (out の変換)
            foreach (var p in f.Params.Where(p => !p.IsOut))
                args.AddRange(InArg(f, p, b, i3));
            foreach (var p in f.Params.Where(p => p.IsOut))
            {
                args.AddRange(OutArg(p.Type, p.LuaName, b, i3));
                if (p.Type.Kind == LubTypeKind.Record && p.Type.Nullable)
                {
                    b.Append($"{i3}bool has_{p.LuaName} = false;\n");
                    args.Add($"&has_{p.LuaName}");
                }
            }
            var r = f.Return;
            var fn = $"LubNative.{NativeFn(ns, f.LuaName)}";
            string retExpr;
            if (f.NoFail)
            {
                if (r.Kind is LubTypeKind.Void or LubTypeKind.Int or LubTypeKind.Double or LubTypeKind.Bool or LubTypeKind.Enum or LubTypeKind.Handle)
                {
                    var call = $"{fn}({string.Join(", ", args)})";
                    if (r.Kind == LubTypeKind.Void) b.Append($"{i3}{call};\n");
                    else b.Append($"{i3}var r = {call};\n");
                    retExpr = r.Kind == LubTypeKind.Void ? "" : FromDirect(r, "r");
                }
                else
                {
                    args.AddRange(OutArg(r, "out", b, i3));
                    b.Append($"{i3}{fn}({string.Join(", ", args)});\n");
                    retExpr = OutValue(r, "out", null);
                }
                foreach (var p in f.Params.Where(p => p.IsOut)) post.Append(OutAssign(p, i3, null));
            }
            else
            {
                var hasRet = r.Kind != LubTypeKind.Void;
                string? has = null;
                if (hasRet)
                {
                    args.AddRange(OutArg(r, "out", b, i3));
                    if (r.Nullable && (r.IsScalar || (r.Kind == LubTypeKind.Record && f.Maybe)))
                    {
                        b.Append($"{i3}bool has = false;\n");
                        args.Add("&has");
                        has = "has";
                    }
                }
                b.Append($"{i3}var st = {fn}({string.Join(", ", args)});\n");
                // NOT_FOUND: 戻り値が null を許す (か戻り値が無い) なら null / 既定値、
                // そうでなければ例外
                var notFound = new StringBuilder();
                notFound.Append($"{i3}if (st == LubNative.LUB_NOT_FOUND)\n{i3}{{\n");
                foreach (var p in f.Params.Where(p => p.IsOut))
                    notFound.Append($"{i3}    {p.Name} = default!;\n");
                if (!hasRet) notFound.Append($"{i3}    return;\n");
                else if (r.Nullable) notFound.Append($"{i3}    return null;\n");
                else if (r.Kind == LubTypeKind.Bool) notFound.Append($"{i3}    return false;\n");
                else notFound.Append($"{i3}    throw new LubException(\"{ns.Name}.{f.Name}: not found\");\n");
                notFound.Append($"{i3}}}\n");
                b.Append(notFound);
                b.Append($"{i3}LubRuntime.Check(st, \"{ns.Name}.{f.Name}\");\n");
                foreach (var p in f.Params.Where(p => p.IsOut))
                    post.Append(OutAssign(p, i3, p.Type.Kind == LubTypeKind.Record && p.Type.Nullable ? $"has_{p.LuaName}" : null));
                retExpr = hasRet ? OutValue(r, "out", has) : "";
            }
            b.Append(post);
            if (retExpr.Length > 0) b.Append($"{i3}return {retExpr};\n");
            b.Append($"{i2}}}\n{i2}finally\n{i2}{{\n{i3}a.End();\n{i2}}}\n");
            sb.Append(b).Append($"{ind}}}\n\n");
        }

        // 入力引数を native の引数列に写す。前処理は b に書く。
        private IEnumerable<string> InArg(ApiFunction f, ApiParam p, StringBuilder b, string ind)
        {
            var n = p.Name;
            var tr = p.Type;
            var v = "_" + p.LuaName;
            switch (tr.Kind)
            {
                case LubTypeKind.Int:
                case LubTypeKind.Double:
                case LubTypeKind.Bool:
                case LubTypeKind.Enum:
                    if (tr.Nullable)
                    {
                        b.Append($"{ind}{NScalar(tr)} {v} = {(tr.Kind == LubTypeKind.Enum ? "(int)" : "")}({n} ?? default);\n");
                        yield return $"{n}.HasValue ? &{v} : null";
                    }
                    else
                        yield return tr.Kind switch
                        {
                            LubTypeKind.Enum => $"(int){n}",
                            LubTypeKind.Bool => $"(byte)({n} ? 1 : 0)",
                            _ => n,
                        };
                    break;
                case LubTypeKind.String:
                    yield return $"a.Str({n})";
                    break;
                case LubTypeKind.Handle:
                    yield return tr.Nullable ? $"({n}?.H ?? 0)" : $"{n}.H";
                    break;
                case LubTypeKind.Keyed:
                    yield return $"a.Str({n}.Key)";
                    break;
                case LubTypeKind.View:
                    if (tr.Nullable)
                    {
                        b.Append($"{ind}if ({n} != null) LubRuntime.CheckView({n}.Frame);\n");
                        b.Append($"{ind}byte* {v} = {n} == null ? null : {n}.Ptr;\n");
                        yield return v;
                        yield return $"{n}?.Length ?? 0";
                    }
                    else
                    {
                        b.Append($"{ind}LubRuntime.CheckView({n}.Frame);\n");
                        yield return $"{n}.Ptr";
                        yield return $"{n}.Length";
                    }
                    break;
                case LubTypeKind.Record:
                    b.Append($"{ind}LubNative.{C(tr.Name)}* {v} = null;\n");
                    b.Append($"{ind}if ({n} != null)\n{ind}{{\n{ind}    {v} = a.Alloc<LubNative.{C(tr.Name)}>(1);\n{ind}    LubNative.To_{C(tr.Name)}({n}, a, {v});\n{ind}}}\n");
                    yield return v;
                    break;
                case LubTypeKind.List:
                    {
                        b.Append($"{ind}int {v}_n = 0;\n");
                        b.Append($"{ind}var {v} = {ListToNative(tr, n, $"{v}_n", p.ArrayLen)};\n");
                        yield return v;
                        yield return $"{v}_n";
                        break;
                    }
                case LubTypeKind.Func:
                    b.Append($"{ind}void* {v}_user = {n} == null ? null : a.Callback({n});\n");
                    yield return $"{n} == null ? null : &LubNative.Tramp_{TrampName(f, p)}";
                    yield return $"{v}_user";
                    break;
                case LubTypeKind.Dict:
                    b.Append($"{ind}int {v}_n = 0;\n{ind}var {v} = a.Bindings({n}, out {v}_n);\n");
                    yield return v;
                    yield return $"{v}_n";
                    break;
                default:
                    throw new InvalidOperationException($"{f.Name}: unsupported parameter {p.Name}: {tr}");
            }
        }

        private static string TrampName(ApiFunction f, ApiParam p) => $"fn_{f.Name}_{p.Name}";

        // List<T> を arena に写す式 (count は countVar に書く)
        private string ListToNative(TypeRef tr, string expr, string countVar, int? arrayLen)
        {
            var elem = tr.Elem!;
            return elem.Kind switch
            {
                LubTypeKind.Double => $"a.Floats({expr}, out {countVar})",
                LubTypeKind.Int => $"a.Ints({expr}, out {countVar})",
                LubTypeKind.Enum => $"a.Ints({expr}, out {countVar})",
                LubTypeKind.Bool => $"a.Bools({expr}, out {countVar})",
                LubTypeKind.String => $"a.Strs({expr}, out {countVar})",
                LubTypeKind.Handle => $"a.Handles({expr}, out {countVar}, static h => h.H)",
                LubTypeKind.Array => $"a.FloatRows({expr}, out {countVar}, {arrayLen ?? throw new InvalidOperationException("List of array needs [LubArray]")})",
                LubTypeKind.Record => $"a.Records<{elem.Name}, LubNative.{C(elem.Name)}>({expr}, out {countVar}, &LubNative.To_{C(elem.Name)})",
                _ => throw new InvalidOperationException($"unsupported list element {elem}"),
            };
        }

        // out / 戻り値の受け皿を宣言し、native の引数列を返す
        private IEnumerable<string> OutArg(TypeRef tr, string n, StringBuilder b, string ind)
        {
            var v = "o_" + n;
            switch (tr.Kind)
            {
                case LubTypeKind.Int:
                case LubTypeKind.Double:
                case LubTypeKind.Bool:
                case LubTypeKind.Enum:
                    b.Append($"{ind}{NScalar(tr)} {v} = default;\n");
                    yield return $"&{v}";
                    break;
                case LubTypeKind.String:
                    b.Append($"{ind}LubNative.LubStr {v} = default;\n");
                    yield return $"&{v}";
                    break;
                case LubTypeKind.Handle:
                    b.Append($"{ind}int {v} = 0;\n");
                    yield return $"&{v}";
                    break;
                case LubTypeKind.View:
                    b.Append($"{ind}LubNative.LubView {v} = default;\n");
                    yield return $"&{v}";
                    break;
                case LubTypeKind.Record:
                    b.Append($"{ind}LubNative.{C(tr.Name)} {v} = default;\n");
                    yield return $"&{v}";
                    break;
                case LubTypeKind.List:
                    b.Append($"{ind}{NElemQualified(tr.Elem!)}* {v} = null;\n{ind}int {v}_n = 0;\n");
                    yield return $"&{v}";
                    yield return $"&{v}_n";
                    break;
                default:
                    throw new InvalidOperationException($"unsupported out / return type {tr}");
            }
        }

        // LubNative の外 (facade の本体) から見た native の要素型
        private static string NElemQualified(TypeRef elem) => elem.Kind switch
        {
            LubTypeKind.String => "LubNative.LubStr",
            LubTypeKind.Record => "LubNative." + C(elem.Name),
            _ => NElem(elem),
        };

        // 受け皿を公開型の値にする式
        private string OutValue(TypeRef tr, string n, string? has)
        {
            var v = "o_" + n;
            var guard = has != null ? $"!{has} ? null : " : "";
            switch (tr.Kind)
            {
                case LubTypeKind.Int:
                    return tr.Nullable ? $"({guard}(int?){v})" : v;
                case LubTypeKind.Double:
                    return tr.Nullable ? $"({guard}(float?){v})" : v;
                case LubTypeKind.Bool:
                    return tr.Nullable ? $"({guard}(bool?){v})" : v;
                case LubTypeKind.Enum:
                    return tr.Nullable ? $"({guard}({EnumCs(tr)}?){v})" : $"({EnumCs(tr)}){v}";
                case LubTypeKind.String:
                    return tr.Nullable ? $"LubRuntime.StrOrNull({v})" : $"LubRuntime.Str({v})";
                case LubTypeKind.Handle:
                    return $"LubNative.H_{tr.Name}({v})";
                case LubTypeKind.View:
                    return $"LubRuntime.View({v})";
                case LubTypeKind.Record:
                    return has != null ? $"({guard}LubNative.From_{C(tr.Name)}(&{v}))" : $"LubNative.From_{C(tr.Name)}(&{v})";
                case LubTypeKind.List:
                    return ListFromNative(tr, v, $"{v}_n");
                default:
                    throw new InvalidOperationException($"unsupported out / return type {tr}");
            }
        }

        private string ListFromNative(TypeRef tr, string ptr, string count)
        {
            var elem = tr.Elem!;
            return elem.Kind switch
            {
                LubTypeKind.Double => $"LubRuntime.FloatList({ptr}, {count})",
                LubTypeKind.Int => $"LubRuntime.IntList({ptr}, {count})",
                LubTypeKind.Enum => $"LubRuntime.EnumList<{EnumCs(elem)}>({ptr}, {count})",
                LubTypeKind.Bool => $"LubRuntime.BoolList({ptr}, {count})",
                LubTypeKind.String => $"LubRuntime.StrList({ptr}, {count})",
                LubTypeKind.Handle => $"LubRuntime.HandleList({ptr}, {count}, LubNative.H_{elem.Name})",
                LubTypeKind.Record => $"LubRuntime.RecordList<{elem.Name}, LubNative.{C(elem.Name)}>({ptr}, {count}, &LubNative.From_{C(elem.Name)})",
                _ => throw new InvalidOperationException($"unsupported list element {elem}"),
            };
        }

        private string OutAssign(ApiParam p, string ind, string? has) =>
            $"{ind}{p.Name} = {OutValue(p.Type, p.LuaName, has)};\n";

        // ----------------------------------------------------------- native

        private void EmitNative()
        {
            sb.Append("internal static unsafe partial class LubNative\n{\n");
            foreach (var t in model.Types.Where(t => t.Kind == "handle"))
                sb.Append($"    internal static {t.Name}? H_{t.Name}(int h) => h == 0 ? null : new {t.Name}(h);\n\n");
            foreach (var t in SortedRecords()) EmitStruct(t);
            foreach (var ns in model.Namespaces)
            {
                foreach (var f in ns.StaticFields)
                    sb.Append($"    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]\n    internal static extern {NReturn(f.Type)} {NativeFn(ns, f.LuaName)}(void* ctx);\n\n");
                foreach (var f in ns.Functions.Where(f => !f.NoC))
                    EmitExtern(ns, f);
            }
            foreach (var t in SortedRecords()) EmitConverters(t);
            foreach (var t in SortedRecords()) EmitFieldTrampolines(t);
            foreach (var ns in model.Namespaces)
                foreach (var f in ns.Functions.Where(f => !f.NoC))
                    foreach (var p in f.Params.Where(p => p.Type.Kind == LubTypeKind.Func))
                        EmitTrampoline(TrampName(f, p), p.Type, 0);
            sb.Append("}\n");
        }

        private List<ApiType> SortedRecords()
        {
            // 依存順 (C# の struct は定義順に依らないが読みやすさのため)
            var records = model.Types.Where(t => t.Kind == "record").ToList();
            var done = new List<ApiType>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Visit(ApiType t)
            {
                if (!seen.Add(t.Name)) return;
                if (t.Base != null && types.TryGetValue(t.Base, out var b)) Visit(b);
                foreach (var f in AllFields(t))
                    foreach (var d in Deps(f.Type))
                        if (types.TryGetValue(d, out var dt) && dt.Kind == "record") Visit(dt);
                done.Add(t);
            }
            foreach (var t in records) Visit(t);
            return done;
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

        private static string NReturn(TypeRef tr) => tr.Kind switch
        {
            LubTypeKind.Void => "void",
            LubTypeKind.Int or LubTypeKind.Enum or LubTypeKind.Handle => "int",
            LubTypeKind.Double => "float",
            LubTypeKind.Bool => "byte",
            _ => throw new InvalidOperationException($"unsupported direct return {tr}"),
        };

        // C の struct と同じ並び (CHeader.EmitStruct と 1 対 1)
        private void EmitStruct(ApiType t)
        {
            var c = C(t.Name);
            sb.Append($"    [StructLayout(LayoutKind.Sequential)]\n    internal struct {c}\n    {{\n");
            var any = false;
            if (t.Base != null)
            {
                sb.Append($"        public {C(t.Base)} @base;\n");
                any = true;
            }
            if (HasFuncs(t))
            {
                sb.Append("        public void* user;\n        public delegate* unmanaged[Cdecl]<void*, void> user_release;\n");
                any = true;
            }
            foreach (var f in t.Fields)
            {
                var n = f.LuaName;
                var tr = f.Type;
                switch (tr.Kind)
                {
                    case LubTypeKind.Int:
                    case LubTypeKind.Double:
                    case LubTypeKind.Bool:
                    case LubTypeKind.Enum:
                        if (f.Optional) sb.Append($"        public bool @has_{n};\n");
                        sb.Append($"        public {NScalar(tr)} @{n};\n");
                        break;
                    case LubTypeKind.String:
                        if (f.Bits)
                        {
                            if (f.Optional) sb.Append($"        public bool @has_{n};\n");
                            sb.Append($"        public ulong @{n};\n");
                        }
                        else sb.Append($"        public LubStr @{n};\n");
                        break;
                    case LubTypeKind.Handle:
                        sb.Append($"        public int @{n};\n");
                        break;
                    case LubTypeKind.Keyed:
                        sb.Append($"        public LubStr @{n};\n");
                        break;
                    case LubTypeKind.View:
                        sb.Append($"        public LubView @{n};\n");
                        break;
                    case LubTypeKind.Record:
                        if (f.Optional) sb.Append($"        public bool @has_{n};\n");
                        sb.Append($"        public {C(tr.Name)} @{n};\n");
                        break;
                    case LubTypeKind.List:
                        {
                            var elem = tr.Elem!;
                            if (elem.Kind == LubTypeKind.Array)
                                sb.Append($"        public {NScalar(elem.Elem!)}* @{n};\n        public int @{n}_count;\n");
                            else if (f.ArrayLen is int len)
                                sb.Append($"        public fixed {NElem(elem)} @{n}[{len}];\n        public int @{n}_count;\n");
                            else
                                sb.Append($"        public {NElem(elem)}* @{n};\n        public int @{n}_count;\n");
                            break;
                        }
                    case LubTypeKind.Array:
                        {
                            var len = f.ArrayLen ?? throw new InvalidOperationException($"{f.Name}: array field needs [LubArray]");
                            if (f.Optional) sb.Append($"        public bool @has_{n};\n");
                            sb.Append($"        public fixed {NScalar(tr.Elem!)} @{n}[{len}];\n");
                            break;
                        }
                    case LubTypeKind.Func:
                        sb.Append($"        public {FnPtr(tr)} @{n};\n");
                        break;
                    default:
                        throw new InvalidOperationException($"{f.Name}: unsupported field type {tr}");
                }
                any = true;
            }
            if (!any) sb.Append("        public int unused;\n");
            sb.Append("    }\n\n");
        }

        private void EmitExtern(ApiNamespace ns, ApiFunction f)
        {
            var args = new List<string> { "void* ctx" };
            foreach (var p in f.Params.Where(p => !p.IsOut))
                args.AddRange(NInParam(f, p));
            foreach (var p in f.Params.Where(p => p.IsOut))
            {
                args.AddRange(NOutParam(p.Type, p.LuaName));
                if (p.Type.Kind == LubTypeKind.Record && p.Type.Nullable)
                    args.Add($"bool* has_{p.LuaName}");
            }
            string ret;
            var r = f.Return;
            if (f.NoFail)
            {
                if (r.Kind is LubTypeKind.Void or LubTypeKind.Int or LubTypeKind.Double or LubTypeKind.Bool or LubTypeKind.Enum or LubTypeKind.Handle)
                    ret = NReturn(r);
                else
                {
                    ret = "void";
                    args.AddRange(NOutParam(r, "out"));
                }
            }
            else
            {
                ret = "int";
                if (r.Kind != LubTypeKind.Void)
                {
                    args.AddRange(NOutParam(r, "out"));
                    if (r.Nullable && (r.IsScalar || (r.Kind == LubTypeKind.Record && f.Maybe)))
                        args.Add("bool* has");
                }
            }
            sb.Append("    [DllImport(LubRuntime.LibName, CallingConvention = CallingConvention.Cdecl)]\n");
            sb.Append($"    internal static extern {ret} {NativeFn(ns, f.LuaName)}({string.Join(", ", args)});\n\n");
        }

        private IEnumerable<string> NInParam(ApiFunction f, ApiParam p)
        {
            var n = "@" + p.LuaName;
            var tr = p.Type;
            switch (tr.Kind)
            {
                case LubTypeKind.Int:
                case LubTypeKind.Double:
                case LubTypeKind.Enum:
                    yield return tr.Nullable ? $"{NScalar(tr)}* {n}" : $"{NScalar(tr)} {n}";
                    break;
                case LubTypeKind.Bool:
                    yield return tr.Nullable ? $"bool* {n}" : $"byte {n}";
                    break;
                case LubTypeKind.String:
                case LubTypeKind.Keyed:
                    yield return $"LubStr {n}";
                    break;
                case LubTypeKind.Handle:
                    yield return $"int {n}";
                    break;
                case LubTypeKind.View:
                    yield return $"byte* {n}";
                    yield return $"int {n}_len";
                    break;
                case LubTypeKind.Record:
                    yield return $"{C(tr.Name)}* {n}";
                    break;
                case LubTypeKind.List:
                    yield return $"{NElem(tr.Elem!)}* {n}";
                    yield return $"int {n}_count";
                    break;
                case LubTypeKind.Func:
                    yield return $"{FnPtr(tr)} {n}";
                    yield return $"void* {n}_user";
                    break;
                case LubTypeKind.Dict:
                    yield return $"LubBinding* {n}";
                    yield return $"int {n}_count";
                    break;
                default:
                    throw new InvalidOperationException($"{f.Name}: unsupported parameter {p.Name}: {tr}");
            }
        }

        private static IEnumerable<string> NOutParam(TypeRef tr, string n)
        {
            n = "@" + n;
            switch (tr.Kind)
            {
                case LubTypeKind.Int:
                case LubTypeKind.Double:
                case LubTypeKind.Bool:
                case LubTypeKind.Enum:
                    yield return $"{NScalar(tr)}* {n}";
                    break;
                case LubTypeKind.String:
                    yield return $"LubStr* {n}";
                    break;
                case LubTypeKind.Handle:
                    yield return $"int* {n}";
                    break;
                case LubTypeKind.View:
                    yield return $"LubView* {n}";
                    break;
                case LubTypeKind.Record:
                    yield return $"{C(tr.Name)}* {n}";
                    break;
                case LubTypeKind.List:
                    yield return $"{NElem(tr.Elem!)}** {n}";
                    yield return $"int* {n}_count";
                    break;
                default:
                    throw new InvalidOperationException($"unsupported out / return type {tr}");
            }
        }

        // ------------------------------------------------------- converters

        private void EmitConverters(ApiType t)
        {
            var c = C(t.Name);
            // To_: 公開 object → native struct (arena に memory を取る)
            sb.Append($"    internal static void To_{c}({t.Name} o, LubRuntime.Arena a, {c}* s)\n    {{\n");
            if (t.Base != null) sb.Append($"        To_{C(t.Base)}(o, a, &s->@base);\n");
            if (HasFuncs(t))
            {
                var funcs = t.Fields.Where(f => f.Type.Kind == LubTypeKind.Func).ToList();
                sb.Append($"        var box = a.CallbackBox(new Delegate?[] {{ {string.Join(", ", funcs.Select(f => "o." + f.Name))} }});\n");
                sb.Append("        s->user = box;\n        s->user_release = box == null ? null : &LubRuntime.ReleaseUser;\n");
            }
            foreach (var f in t.Fields)
            {
                var n = f.LuaName;
                var tr = f.Type;
                var v = "o." + f.Name;
                switch (tr.Kind)
                {
                    case LubTypeKind.Int:
                    case LubTypeKind.Double:
                    case LubTypeKind.Bool:
                    case LubTypeKind.Enum:
                        {
                            var cast = tr.Kind == LubTypeKind.Double ? "(float)" : tr.Kind == LubTypeKind.Enum ? "(int)" : "";
                            if (f.Optional)
                                sb.Append($"        s->@has_{n} = {v}.HasValue;\n        s->@{n} = {cast}({v} ?? default);\n");
                            else
                                sb.Append($"        s->@{n} = {cast}{v};\n");
                            break;
                        }
                    case LubTypeKind.String:
                        if (f.Bits)
                        {
                            if (f.Optional)
                                sb.Append($"        s->@has_{n} = {v} != null;\n        s->@{n} = LubRuntime.Bits({v});\n");
                            else
                                sb.Append($"        s->@{n} = LubRuntime.Bits({v});\n");
                        }
                        else sb.Append($"        s->@{n} = a.Str({v});\n");
                        break;
                    case LubTypeKind.Handle:
                        sb.Append($"        s->@{n} = {v}?.H ?? 0;\n");
                        break;
                    case LubTypeKind.Keyed:
                        sb.Append($"        s->@{n} = a.Str({v}?.Key);\n");
                        break;
                    case LubTypeKind.View:
                        sb.Append($"        s->@{n} = LubRuntime.ViewOf({v});\n");
                        break;
                    case LubTypeKind.Record:
                        if (f.Optional)
                            sb.Append($"        s->@has_{n} = {v} != null;\n        if ({v} != null) To_{C(tr.Name)}({v}, a, &s->@{n});\n");
                        else
                            sb.Append($"        if ({v} != null) To_{C(tr.Name)}({v}, a, &s->@{n});\n");
                        break;
                    case LubTypeKind.List:
                        {
                            var elem = tr.Elem!;
                            if (elem.Kind != LubTypeKind.Array && f.ArrayLen is int len)
                            {
                                sb.Append($"        s->@{n}_count = LubRuntime.Fixed{FixedKind(elem)}({v}, s->@{n}, {len});\n");
                            }
                            else
                            {
                                sb.Append($"        s->@{n} = {ListToNative(tr, v, $"s->@{n}_count", f.ArrayLen)};\n");
                            }
                            break;
                        }
                    case LubTypeKind.Array:
                        {
                            var len = f.ArrayLen!.Value;
                            if (f.Optional)
                                sb.Append($"        s->@has_{n} = {v} != null;\n");
                            sb.Append($"        LubRuntime.Fixed{FixedKind(tr.Elem!)}({v}, s->@{n}, {len});\n");
                            break;
                        }
                    case LubTypeKind.Func:
                        {
                            var slot = t.Fields.Where(x => x.Type.Kind == LubTypeKind.Func).ToList().IndexOf(f);
                            sb.Append($"        s->@{n} = {v} == null ? null : &Tramp_{c}_{n};\n");
                            _ = slot;
                            break;
                        }
                    default:
                        throw new InvalidOperationException($"{t.Name}.{f.Name}: unsupported {tr}");
                }
            }
            sb.Append("    }\n\n");

            // Fill_ / From_: native struct → 公開 object
            sb.Append($"    internal static void Fill_{c}({t.Name} o, {c}* s)\n    {{\n");
            if (t.Base != null) sb.Append($"        Fill_{C(t.Base)}(o, &s->@base);\n");
            foreach (var f in t.Fields)
            {
                var n = f.LuaName;
                var tr = f.Type;
                var v = "o." + f.Name;
                switch (tr.Kind)
                {
                    case LubTypeKind.Int:
                    case LubTypeKind.Bool:
                        sb.Append(f.Optional ? $"        {v} = s->@has_{n} ? s->@{n} : null;\n" : $"        {v} = s->@{n};\n");
                        break;
                    case LubTypeKind.Double:
                        sb.Append(f.Optional ? $"        {v} = s->@has_{n} ? s->@{n} : null;\n" : $"        {v} = s->@{n};\n");
                        break;
                    case LubTypeKind.Enum:
                        sb.Append(f.Optional ? $"        {v} = s->@has_{n} ? ({EnumCs(tr)})s->@{n} : null;\n" : $"        {v} = ({EnumCs(tr)})s->@{n};\n");
                        break;
                    case LubTypeKind.String:
                        if (f.Bits)
                            sb.Append(f.Optional ? $"        {v} = s->@has_{n} ? LubRuntime.BitsStr(s->@{n}) : null;\n" : $"        {v} = LubRuntime.BitsStr(s->@{n});\n");
                        else
                            sb.Append(tr.Nullable ? $"        {v} = LubRuntime.StrOrNull(s->@{n});\n" : $"        {v} = LubRuntime.Str(s->@{n});\n");
                        break;
                    case LubTypeKind.Handle:
                        sb.Append($"        {v} = H_{tr.Name}(s->@{n}){(tr.Nullable ? "" : "!")};\n");
                        break;
                    case LubTypeKind.Keyed:
                        sb.Append($"        {v} = (s->@{n}.len > 0 ? new {tr.Name}(LubRuntime.Str(s->@{n})) : null){(tr.Nullable ? "" : "!")};\n");
                        break;
                    case LubTypeKind.View:
                        sb.Append($"        {v} = LubRuntime.View(s->@{n}){(tr.Nullable ? "" : "!")};\n");
                        break;
                    case LubTypeKind.Record:
                        if (f.Optional)
                            sb.Append($"        {v} = s->@has_{n} ? From_{C(tr.Name)}(&s->@{n}) : null;\n");
                        else
                            sb.Append($"        {v} = From_{C(tr.Name)}(&s->@{n});\n");
                        break;
                    case LubTypeKind.List:
                        {
                            // 無い配列 (NULL) は Lua と同じく null にする (空 list に
                            // しない。ゲームは有無で分岐する)
                            var elem = tr.Elem!;
                            if (elem.Kind == LubTypeKind.Array)
                                sb.Append($"        {v} = s->@{n} == null ? null! : LubRuntime.FloatRowList(s->@{n}, s->@{n}_count, {f.ArrayLen});\n");
                            else if (f.ArrayLen is int)
                                sb.Append($"        {v} = {ListFromNative(tr, $"s->@{n}", $"s->@{n}_count")};\n");
                            else
                                sb.Append($"        {v} = s->@{n} == null ? null! : {ListFromNative(tr, $"s->@{n}", $"s->@{n}_count")};\n");
                            break;
                        }
                    case LubTypeKind.Array:
                        {
                            var len = f.ArrayLen!.Value;
                            var arr = $"LubRuntime.{FixedKind(tr.Elem!)}Array(s->@{n}, {len})";
                            sb.Append(f.Optional ? $"        {v} = s->@has_{n} ? {arr} : null;\n" : $"        {v} = {arr};\n");
                            break;
                        }
                    case LubTypeKind.Func:
                        break; // callback は戻さない
                    default:
                        throw new InvalidOperationException($"{t.Name}.{f.Name}: unsupported {tr}");
                }
            }
            sb.Append("    }\n\n");
            sb.Append($"    internal static {t.Name} From_{c}({c}* s)\n    {{\n        var o = new {t.Name}();\n        Fill_{c}(o, s);\n        return o;\n    }}\n\n");
        }

        private static string FixedKind(TypeRef elem) => elem.Kind switch
        {
            LubTypeKind.Double => "Floats",
            LubTypeKind.Int or LubTypeKind.Enum => "Ints",
            _ => throw new InvalidOperationException($"unsupported fixed array element {elem}"),
        };

        // ------------------------------------------------------ trampolines

        private void EmitFieldTrampolines(ApiType t)
        {
            var c = C(t.Name);
            var funcs = t.Fields.Where(f => f.Type.Kind == LubTypeKind.Func).ToList();
            for (var i = 0; i < funcs.Count; i++)
                EmitTrampoline($"{c}_{funcs[i].LuaName}", funcs[i].Type, i);
        }

        // C から呼ばれる static 関数。user は LubRuntime.CallbackBox の GCHandle。
        private void EmitTrampoline(string name, TypeRef fn, int slot)
        {
            var ret = fn.FuncReturn!;
            var retC = ret.Kind switch
            {
                LubTypeKind.Bool => "byte",
                LubTypeKind.Double => "float",
                LubTypeKind.Int => "int",
                LubTypeKind.Void => "void",
                _ => throw new InvalidOperationException($"callback {name}: return {ret}"),
            };
            var ps = fn.FuncParams!;
            var args = new List<string> { "void* user" };
            var conv = new List<string>();
            for (var i = 0; i < ps.Count; i++)
            {
                var p = ps[i];
                var pn = (char)('a' + i);
                args.Add(p.Kind switch
                {
                    LubTypeKind.Record => $"{C(p.Name)}* {pn}",
                    LubTypeKind.Int or LubTypeKind.Enum => $"int {pn}",
                    LubTypeKind.Double => $"float {pn}",
                    LubTypeKind.Bool => $"byte {pn}",
                    _ => throw new InvalidOperationException($"callback {name}: param {p}"),
                });
                conv.Add(p.Kind switch
                {
                    LubTypeKind.Record => $"From_{C(p.Name)}({pn})",
                    LubTypeKind.Enum => $"({EnumCs(p)}){pn}",
                    LubTypeKind.Double => pn.ToString(),
                    LubTypeKind.Bool => $"({pn} != 0)",
                    _ => pn.ToString(),
                });
            }
            // 既定値: 判定は「続行 / 衝突する」、数値は 0 (runtime が既定に読み替える)。
            var def = ret.Kind switch
            {
                LubTypeKind.Bool => "1",
                LubTypeKind.Double => "1.0f",
                LubTypeKind.Int => "1",
                _ => "",
            };
            var delegateType = Cs(fn with { Nullable = false });
            sb.Append("    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]\n");
            sb.Append($"    internal static {retC} Tramp_{name}({string.Join(", ", args)})\n    {{\n");
            sb.Append($"        var box = LubRuntime.CallbackBox.From(user);\n");
            sb.Append($"        var cb = ({delegateType}?)box.Slots[{slot}];\n");
            sb.Append($"        if (cb == null) return{(def.Length > 0 ? " " + def : "")};\n");
            sb.Append("        try\n        {\n");
            var call = $"cb({string.Join(", ", conv)})";
            sb.Append(ret.Kind switch
            {
                LubTypeKind.Void => $"            {call};\n",
                LubTypeKind.Bool => $"            return (byte)({call} ? 1 : 0);\n",
                LubTypeKind.Double => $"            return (float){call};\n",
                _ => $"            return {call};\n",
            });
            sb.Append("        }\n        catch (Exception e)\n        {\n");
            sb.Append($"            box.Fail({slot}, \"{name}\", e);\n");
            if (def.Length > 0) sb.Append($"            return {def};\n");
            sb.Append("        }\n    }\n\n");
        }
    }
}
