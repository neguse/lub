using System.Globalization;
using System.Text;
using TinyCs.Backend;

namespace TinyCs.Tcs2c;

// 型記述子 (doc/object-model.md §4) を VM と同じ byte 列で生成 C に置く: tcs_type_table。
// TCS_VERIFY_TYPES の build では、初期化で runtime/model がこの表を読み、C の struct の規則 (§5) で求めた
// field の offset が、生成した struct の offsetof と一致することを確かめる (M-2)。
// 表は heap の walker (§7) と reload の計画 (§6) の入力にもなる。
internal sealed partial class CEmitter
{
    private sealed class TypeTableBuilder
    {
        public readonly List<TypeTable.Entry> Entries = [];
        public readonly Dictionary<string, ushort> Index = [];
        /// <summary>offset の検査: (型の番号、field の番号、C の型の名前、C の field の名前)。</summary>
        public readonly List<(int Type, int Field, string Owner, string Member)> Checks = [];
    }

    private void EmitTypeTable()
    {
        var b = new TypeTableBuilder();
        foreach (var st in StructInfos)
            StructEntry(b, CType.Struct(st.Name));
        foreach (var cls in _program.Classes.Where(c => !c.IsInterface))
            ClassEntry(b, cls);

        // string 表と型の表 (VM の .tcsb と同じ形)
        var strings = new List<string>();
        var stringIndex = new Dictionary<string, int>();
        int Str(string s)
        {
            if (stringIndex.TryGetValue(s, out var i)) return i;
            stringIndex[s] = strings.Count;
            strings.Add(s);
            return strings.Count - 1;
        }
        foreach (var name in TypeTable.Names(b.Entries)) Str(name);
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(strings.Count);
        foreach (var s in strings)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            w.Write(bytes.Length);
            w.Write(bytes);
        }
        TypeTable.Write(w, b.Entries, Str);
        var data = ms.ToArray();

        Line("/* 型記述子 (doc/object-model.md §4。VM の .tcsb の types と同じ形式) */");
        Line($"static const unsigned char tcs_type_table[{Math.Max(data.Length, 1)}] = {{");
        _indent++;
        for (var i = 0; i < data.Length; i += 24)
            Line(string.Join(", ", data.Skip(i).Take(24).Select(x => x.ToString(CultureInfo.InvariantCulture))) + ",");
        _indent--;
        Line("};");
        Line("#ifdef TCS_VERIFY_TYPES");
        _output.Append(RuntimeModel.Value);
        Line("static void");
        Line("tcs_verify_types(void)");
        Line("{");
        _indent++;
        Line("static const struct { uint16_t type, field; uint32_t offset; } expect[] = {");
        _indent++;
        foreach (var (type, field, owner, member) in b.Checks)
            Line($"{{ {type}, {field}, (uint32_t)offsetof({owner}, {member}) }},");
        Line("{ 0xFFFF, 0, 0 }");
        _indent--;
        Line("};");
        Line("TcsReader r = { tcs_type_table, tcs_type_table + sizeof tcs_type_table, 0 };");
        Line("char **strings = NULL;");
        Line("uint32_t nstrings = 0, ntypes = 0;");
        Line("TcsType *types = NULL;");
        Line("const char *err = tcs_model_read_strings(&r, &strings, &nstrings);");
        Line("if (!err) err = tcs_model_read(&r, (const char *const *)strings, nstrings, &ntypes, &types);");
        // class の payload は type_id (uint32_t) の後から (§3、§5)
        Line("if (!err) err = tcs_model_layout(types, ntypes, (uint32_t)sizeof(uint32_t), (uint32_t)sizeof(void *));");
        Line("if (err) { fprintf(stderr, \"type table: %s\\n\", err); abort(); }");
        Line("for (size_t i = 0; expect[i].type != 0xFFFF; i++) {");
        _indent++;
        Line("const TcsField *f = &types[expect[i].type].fields[expect[i].field];");
        Line("if (f->offset != expect[i].offset) {");
        _indent++;
        Line("fprintf(stderr, \"type table: %s.%s at %u, the C struct has %u\\n\", types[expect[i].type].name, f->name, (unsigned)f->offset, (unsigned)expect[i].offset);");
        Line("abort();");
        _indent--;
        Line("}");
        _indent--;
        Line("}");
        Line("tcs_model_free(types, ntypes);");
        Line("tcs_model_free_strings(strings, nstrings);");
        _indent--;
        Line("}");
        Line("#endif");
        Line();
    }

    private TypeTable.Info FieldInfo(TypeTableBuilder b, CType t) => t.Kind switch
    {
        CTypeKind.I32 => new(TypeTable.KindI32, TypeTable.NoType),
        CTypeKind.F32 => new(TypeTable.KindF32, TypeTable.NoType),
        CTypeKind.Bool => new(TypeTable.KindBool, TypeTable.NoType),
        CTypeKind.StructVal or CTypeKind.Nullable => new(TypeTable.KindStruct, StructEntry(b, t)),
        _ => new(TypeTable.KindRef, TypeTable.NoType),
    };

    /// <summary>値型 (user の struct、T?) の記述子。user の struct は offsetof で検査する。</summary>
    private ushort StructEntry(TypeTableBuilder b, CType t)
    {
        var key = t.CName;
        if (b.Index.TryGetValue(key, out var i)) return i;
        List<(string Name, CType Type, string Member)> fields;
        IlStructInfo? info = null;
        if (t.Kind == CTypeKind.Nullable)
            fields = [("HasValue", CType.Bool, "has"), ("Value", t.Element!, "v")];
        else if (StructInfos.FirstOrDefault(s => s.Name == t.Name) is { } st)
        {
            info = st;
            fields = [.. st.Fields.Where(f => !f.IsStatic).Select(f => (f.Name, _facts.MapType(f.Type), Names.Field(f.Name)))];
        }
        else
            throw new Tcs2cException($"type table: no fields for {t.CName}");
        var entryFields = fields.Select(f => new TypeTable.Field(f.Name, FieldInfo(b, f.Type))).ToList();
        i = (ushort)b.Entries.Count;
        b.Entries.Add(new TypeTable.Entry(TypeTable.Struct, t.Name ?? t.CName)
        {
            LayoutHash = TypeTable.ParseLayoutHash(info?.LayoutHash),
            Flags = info?.IsRecord == true ? TypeTable.FlagRecord : (byte)0,
            Fields = [.. entryFields],
        });
        b.Index[key] = i;
        for (var k = 0; k < fields.Count; k++) b.Checks.Add((i, k, t.CName, fields[k].Member));
        return i;
    }

    private void ClassEntry(TypeTableBuilder b, IlClassInfo cls)
    {
        // host と共有する外部 data class は payload の先頭に host_value を持つ (§3)。v1 の検査は外す
        var external = ChainRootFirst(cls.Name).Any(c => c.IsExternal);
        var fields = ChainRootFirst(cls.Name)
            .SelectMany(link => link.Fields.Where(f => !f.IsStatic).Select(f => (Link: link, Field: f)))
            .ToList();
        var entryFields = fields
            .Select(x => new TypeTable.Field(x.Field.Name, FieldInfo(b, _facts.Field(x.Link.Name, x.Field.Name).Type)))
            .ToList();
        var i = (ushort)b.Entries.Count;
        b.Entries.Add(new TypeTable.Entry(TypeTable.Class, cls.Name)
        {
            LayoutHash = TypeTable.ParseLayoutHash(cls.LayoutHash),
            Flags = (byte)((cls.IsRecord ? TypeTable.FlagRecord : 0)
                | (cls.Methods.Any(m => m.Name == "OnReload" && !m.IsStatic) ? TypeTable.FlagHasOnReload : 0)),
            Fields = [.. entryFields],
        });
        b.Index["class:" + cls.Name] = i;
        if (external) return;
        for (var k = 0; k < fields.Count; k++)
            b.Checks.Add((i, k, $"struct {Names.Class(cls.Name)}", Names.Field(fields[k].Field.Name)));
    }
}
