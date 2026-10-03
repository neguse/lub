using System.Text;
using TinyCs;

namespace TinyCs.Tcs2c;

// GC 向けの生成物: class / struct の pointer map (TcsLayout)、static field
// の root 走査関数、string literal の static object 化。runtime 側の
// 契約は CRuntime.Gc (kind + layout で精密 trace、stack は保守的走査)。
internal sealed partial class CEmitter
{
    private const string LiteralsMarker = "/*__TCS2C_LITERALS__*/";
    private readonly Dictionary<string, string> _literals = new(StringComparer.Ordinal);
    private readonly List<string> _literalDecls = [];

    // 確保時に runtime へ渡す要素 / 値の layout 参照
    private string LayoutRef(CType type) => type.Kind switch
    {
        CTypeKind.I32 or CTypeKind.F32 or CTypeKind.Bool => "NULL",
        CTypeKind.String or CTypeKind.Ref or CTypeKind.Array or CTypeKind.List
            or CTypeKind.Dict or CTypeKind.Closure or CTypeKind.Random
            or CTypeKind.Object => "&tcs_layout_ptr",
        CTypeKind.StructVal => $"&{Names.StructLayout(type.Name!)}",
        CTypeKind.Nullable => type.Element!.Kind == CTypeKind.StructVal
            ? $"&{Names.NullableLayout(type.Element.Name!)}" : "NULL",
        _ => throw new Tcs2cException($"type has no storage layout: {type}"),
    };

    private static bool IsPointerType(CType type) => type.Kind
        is CTypeKind.String or CTypeKind.Ref or CTypeKind.Array or CTypeKind.List
        or CTypeKind.Dict or CTypeKind.Closure or CTypeKind.Random or CTypeKind.Object;

    // struct 値 1 個の中の pointer slot (byte offset 式)。struct-in-struct は
    // offsetof の加算で平坦化する
    private IEnumerable<string> StructPointerSlots(string structName)
    {
        var st = _facts.Structs[structName];
        var cName = Names.Class(structName);
        foreach (var field in st.Fields)
        {
            var type = _facts.MapType(field.Type);
            var self = $"offsetof({cName}, {Names.Field(field.Name)})";
            if (IsPointerType(type))
                yield return self;
            else if (type.Kind == CTypeKind.StructVal)
                foreach (var inner in StructPointerSlots(type.Name!))
                    yield return $"{self} + {inner}";
            else if (type is { Kind: CTypeKind.Nullable, Element.Kind: CTypeKind.StructVal })
                foreach (var inner in StructPointerSlots(type.Element.Name!))
                    yield return $"{self} + offsetof({type.CName}, v) + {inner}";
        }
    }

    private IEnumerable<string> ClassPointerSlots(string className)
    {
        var cName = Names.Class(className);
        foreach (var link in ChainRootFirst(className))
        foreach (var field in link.Fields.Where(f => !f.IsStatic))
        {
            var type = _facts.Field(link.Name, field.Name).Type;
            var self = $"offsetof({cName}, {Names.Field(field.Name)})";
            if (IsPointerType(type))
                yield return self;
            else if (type.Kind == CTypeKind.StructVal)
                foreach (var inner in StructPointerSlots(type.Name!))
                    yield return $"{self} + {inner}";
            else if (type is { Kind: CTypeKind.Nullable, Element.Kind: CTypeKind.StructVal })
                foreach (var inner in StructPointerSlots(type.Element.Name!))
                    yield return $"{self} + offsetof({type.CName}, v) + {inner}";
        }
    }

    private void EmitLayout(string layoutName, string cName,
        IReadOnlyList<string> slots)
    {
        if (slots.Count == 0)
        {
            Line($"static const TcsLayout {layoutName} = {{ sizeof({cName}), 0, NULL }};");
            return;
        }
        Line($"static const uint32_t {layoutName}_offsets[] = {{");
        _indent++;
        foreach (var slot in slots) Line($"{slot},");
        _indent--;
        Line("};");
        Line($"static const TcsLayout {layoutName} = " +
            $"{{ sizeof({cName}), {slots.Count}, {layoutName}_offsets }};");
    }

    // struct typedef (EmitStructTypedefs) と class 定義の後に呼ぶ
    private void EmitLayouts()
    {
        if (!_program.Structs.IsDefault)
            foreach (var st in _program.Structs)
            {
                EmitLayout(Names.StructLayout(st.Name), Names.Class(st.Name),
                    [.. StructPointerSlots(st.Name)]);
                var option = CType.Nullable(CType.Struct(st.Name));
                EmitLayout(Names.NullableLayout(st.Name), option.CName,
                    [.. StructPointerSlots(st.Name)
                        .Select(inner => $"offsetof({option.CName}, v) + {inner}")]);
            }
        foreach (var cls in _program.Classes)
            EmitLayout(Names.ClassLayout(cls.Name), Names.Class(cls.Name),
                [.. ClassPointerSlots(cls.Name)]);
        Line();
    }

    // 参照を含み得る格納型 (store にライトバリアが要る)
    private static bool NeedsBarrier(CType type) =>
        IsPointerType(type) || type.Kind == CTypeKind.StructVal
        || type is { Kind: CTypeKind.Nullable, Element.Kind: CTypeKind.StructVal };

    // struct 連鎖 / field / 要素 place を所有する heap object の式 (stack 上の
    // 値なら null)。struct method の self は呼び出し側から渡る v_owner
    private string? RenderStructOwner(IlExpr place) => place switch
    {
        IlVar v when v.Name == "self" && _structSelfOwner => "v_owner",
        IlVar v => Resolve(v.Name) is { Boxed: true } cell ? cell.CName : null,
        IlField f when TryStaticField(f, out _, out _) => null,
        IlField f => TypeOf(f.Recv).Kind == CTypeKind.StructVal
            ? RenderStructOwner(f.Recv)
            : $"tcs_nonnull({RenderExpr(f.Recv)})",
        IlIndex index => RenderExpr(index.Recv),
        _ => null,
    };

    // struct method の本文中か (self の所有者は v_owner 引数)
    private bool _structSelfOwner;

    // static field の root 走査 (旧世代 mark-sweep) と境界の昇格
    private void EmitStaticRoots()
    {
        Line("static void");
        Line("tcs_gc_forward_statics(void)");
        Line("{");
        _indent++;
        foreach (var cls in _program.Classes)
        foreach (var field in cls.Fields.Where(f => f.IsStatic))
        {
            var fact = _facts.Field(cls.Name, field.Name);
            var name = Names.StaticField(cls.Name, field.Name);
            if (IsPointerType(fact.Type))
                Line($"{name} = tcs_gc_promote({name});");
            else if (fact.Type.Kind == CTypeKind.StructVal
                || fact.Type is { Kind: CTypeKind.Nullable, Element.Kind: CTypeKind.StructVal })
                Line($"tcs_gc_forward_value(&{name}, {LayoutRef(fact.Type)});");
        }
        _indent--;
        Line("}");
        Line();
        Line("static void");
        Line("tcs_gc_mark_statics(void)");
        Line("{");
        _indent++;
        foreach (var cls in _program.Classes)
        foreach (var field in cls.Fields.Where(f => f.IsStatic))
        {
            var fact = _facts.Field(cls.Name, field.Name);
            var name = Names.StaticField(cls.Name, field.Name);
            if (IsPointerType(fact.Type))
                Line($"tcs_gc_mark_ptr({name});");
            else if (fact.Type.Kind == CTypeKind.StructVal
                || fact.Type is { Kind: CTypeKind.Nullable, Element.Kind: CTypeKind.StructVal })
                Line($"tcs_gc_mark_value(&{name}, {LayoutRef(fact.Type)});");
        }
        _indent--;
        Line("}");
        Line();
    }

    // string literal は評価ごとに確保せず、TCS_GC_STATIC な file-scope object
    // を共有する (TcsString と layout 互換の { header; length; data })
    private string InternStringLiteral(byte[] bytes)
    {
        var key = Convert.ToHexString(bytes);
        if (_literals.TryGetValue(key, out var existing)) return existing;
        var name = $"tcs_lit_{_literals.Count}";
        var escaped = string.Concat(bytes.Select(b => $"\\x{b:x2}"));
        _literalDecls.Add(
            $"static const struct {{ TcsGcHeader h; size_t length; " +
            $"unsigned char data[{bytes.Length + 1}]; }} {name} = " +
            $"{{ {{ NULL, NULL, sizeof(size_t) + {bytes.Length}, TCS_KIND_RAW, " +
            $"TCS_GC_STATIC, TCS_TYPE_STRING }}, {bytes.Length}, \"{escaped}\" }};");
        var reference = $"((TcsString *)&{name}.length)";
        _literals[key] = reference;
        return reference;
    }
}
