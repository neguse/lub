using System.Text;
using TinyCs;

namespace TinyCs.Tcs2c;

// record class: 構造等価 (==/!=) と with 式。Lua backend は __eq metamethod
// と shallow copy IIFE で、C は record 型ごとの比較関数と runtime layout
// (実行時型) での copy で同じ意味論にする。
internal sealed partial class CEmitter
{
    private bool IsRecordClass(string name) =>
        _classes.TryGetValue(name, out var cls) && cls.IsRecord;

    // ==/!= の両辺が class 参照で、静的型の chain に record があれば構造等価
    private string? RecordEqualityType(CType left, CType right)
    {
        if (left.Kind != CTypeKind.Ref || right.Kind != CTypeKind.Ref) return null;
        var common = IsAncestorOrSame(left.Name!, right.Name!) ? left.Name!
            : IsAncestorOrSame(right.Name!, left.Name!) ? right.Name! : null;
        return common != null && IsRecordClass(common) ? common : null;
    }

    private void EmitRecordEqualityPrototypes()
    {
        foreach (var st in _facts.Structs.Keys)
            Line($"static bool {Names.StructEq(st)}(const {Names.Class(st)} *a, " +
                $"const {Names.Class(st)} *b);");
        foreach (var cls in _program.Classes.Where(c => c.IsRecord))
            Line($"static bool {Names.RecordEq(cls.Name)}({Names.Class(cls.Name)} *a, " +
                $"{Names.Class(cls.Name)} *b);");
    }

    // field 1 個の等価式 (a->f と b->f、または a.f と b.f)
    private string FieldEqualExpr(CType type, string left, string right) => type.Kind switch
    {
        CTypeKind.String => $"tcs_string_equal({left}, {right})",
        CTypeKind.Ref when IsRecordClass(type.Name!) =>
            $"{Names.RecordEq(type.Name!)}({left}, {right})",
        CTypeKind.StructVal => $"{Names.StructEq(type.Name!)}(&{left}, &{right})",
        CTypeKind.Nullable => NullableEqualExpr(type, left, right),
        _ => $"({left} == {right})",
    };

    private void EmitRecordEquality()
    {
        // データ struct は memberwise (C# の ValueType.Equals と同じ)
        foreach (var st in _program.Structs.IsDefault ? [] : _program.Structs)
        {
            var cName = Names.Class(st.Name);
            Line("static bool");
            Line($"{Names.StructEq(st.Name)}(const {cName} *a, const {cName} *b)");
            Line("{");
            _indent++;
            var parts = st.Fields.Select(f => FieldEqualExpr(_facts.MapType(f.Type),
                $"a->{Names.Field(f.Name)}", $"b->{Names.Field(f.Name)}")).ToList();
            Line($"return {(parts.Count == 0 ? "true" : string.Join(" && ", parts))};");
            _indent--;
            Line("}");
            Line();
        }
        foreach (var cls in _program.Classes.Where(c => c.IsRecord))
        {
            var cName = Names.Class(cls.Name);
            Line("static bool");
            Line($"{Names.RecordEq(cls.Name)}({cName} *a, {cName} *b)");
            Line("{");
            _indent++;
            Line("if (a == b) return true;");
            Line("if (a == NULL || b == NULL) return false;");
            Line("if (a->type_id != b->type_id) return false;");
            // 派生 record は実行時型の比較関数へ (全 field を見る)
            var derived = _program.Classes
                .Where(c => c.Name != cls.Name && IsAncestorOrSame(cls.Name, c.Name))
                .ToList();
            if (derived.Count > 0)
            {
                Line("switch (a->type_id) {");
                foreach (var d in derived)
                    Line($"case {Names.TypeId(d.Name)}: return {Names.RecordEq(d.Name)}(" +
                        $"({Names.Class(d.Name)} *)a, ({Names.Class(d.Name)} *)b);");
                Line("default: break;");
                Line("}");
            }
            var parts = new List<string>();
            foreach (var link in ChainRootFirst(cls.Name))
            foreach (var field in link.Fields.Where(f => !f.IsStatic))
                parts.Add(FieldEqualExpr(_facts.Field(link.Name, field.Name).Type,
                    $"a->{Names.Field(field.Name)}", $"b->{Names.Field(field.Name)}"));
            Line($"return {(parts.Count == 0 ? "true" : string.Join(" && ", parts))};");
            _indent--;
            Line("}");
            Line();
        }
    }

    private CType TypeOfWith(IlWith with)
    {
        var source = TypeOf(with.Src);
        if (source.Kind == CTypeKind.StructVal) return TypeOfStructWith(with, source);
        if (source.Kind != CTypeKind.Ref)
            throw new Tcs2cException($"with source is not a class reference: {source}");
        foreach (var (name, value) in with.Overrides)
            CheckAssignable(FieldInChain(source.Name!, name).Type, value, $"with {name}");
        return source;
    }

    // shallow copy は実行時型の layout で行う (派生 record の with も型を保つ)
    private string RenderWith(IlWith with)
    {
        var type = TypeOfWith(with);
        if (type.Kind == CTypeKind.StructVal) return RenderStructWith(with, type);
        var src = Temp("with_src");
        var copy = Temp("with_copy");
        var header = Temp("with_header");
        var sb = new StringBuilder();
        sb.Append($"{type.CName} {src} = ({type.CName})tcs_nonnull({RenderExpr(with.Src)}); ");
        sb.Append($"TcsGcHeader *{header} = TCS_GC_HEADER({src}); ");
        sb.Append($"{type.CName} {copy} = tcs_new_object({header}->layout); ");
        sb.Append($"memcpy({copy}, {src}, {header}->layout->size); ");
        sb.Append($"TCS_GC_HEADER({copy})->type_id = {header}->type_id; ");
        foreach (var (name, value) in with.Overrides)
        {
            var fieldType = FieldInChain(type.Name!, name).Type;
            sb.Append($"{copy}->{Names.Field(name)} = {RenderCoerced(value, fieldType)}; ");
        }
        return $"({{ {sb}{copy}; }})";
    }
}
