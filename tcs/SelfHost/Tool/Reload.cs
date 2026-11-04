using System.Collections.Generic;

namespace TcsSelf;

// hot reload で live に当てられない編集の規則 (doc/hot-reload-design.md §11 の規則表)。
// reload chunk (IL の metadata) と module registry (増分 session の型の記録) が、それぞれの記録をこの形に写して同じ規則で判定する
public sealed class ReloadStatic
{
    public string Name;
    public string Type;
    public string? InitHash;
    public bool Pure;

    public ReloadStatic(string name, string type, string? initHash, bool pure)
    {
        Name = name;
        Type = type;
        InitHash = initHash;
        Pure = pure;
    }
}

public sealed class ReloadType
{
    public string Name;
    // class | record | struct | enum
    public string Kind;
    public string BaseName = "";
    public string InstanceShape = "";
    public List<ReloadStatic> Statics = new List<ReloadStatic>();
    // instance の field (Notes が初期化子の変更を見る。restart の判定には使わない)
    public List<ReloadStatic> InstanceFields = new List<ReloadStatic>();
    public List<string> EnumMemberNames = new List<string>();
    public List<int> EnumMemberValues = new List<int>();

    public ReloadType(string name, string kind)
    {
        Name = name;
        Kind = kind;
    }
}

public static class ReloadRules
{
    // 戻り値は restart の理由で、空なら live。migratesInstances は reload chunk: instance の field を registry で移し、
    // 新しい static の初期化子を chunk の中で評価するので、instance の形の変更と副作用のある static の追加を restart にしない。
    // module registry (false) は既存の instance を移さず、transaction の中で副作用のある初期化子を実行しないので、どちらも restart にする
    public static List<string> RestartReasons(List<ReloadType> v1, List<ReloadType> v2, bool migratesInstances)
    {
        var reasons = new List<string>();
        var next = new Dictionary<string, ReloadType>();
        foreach (var t in v2) next[t.Name] = t;
        foreach (var old in v1)
        {
            ReloadType? t;
            if (!next.TryGetValue(old.Name, out t))
            {
                reasons.Add("type removed: " + old.Name);
                continue;
            }
            if (old.Kind != t.Kind)
            {
                reasons.Add("type kind changed: " + t.Name);
                continue;
            }
            if (old.BaseName != t.BaseName) reasons.Add("base changed: " + t.Name);
            if (!migratesInstances && old.InstanceShape != t.InstanceShape) reasons.Add("instance shape changed: " + t.Name);
            StaticReasons(old, t, migratesInstances, reasons);
            EnumReasons(old, t, reasons);
        }
        return reasons;
    }

    // 既存の static は今の値を保つので、初期化子の変更は反映されない。型の変更も restart (保った値は旧い型のまま)。
    // const も static と同じ (使う側には埋め込まれるが、それを使う static の初期化子は評価し直さない)
    private static void StaticReasons(ReloadType old, ReloadType t, bool migratesInstances, List<string> reasons)
    {
        var oldStatics = new Dictionary<string, ReloadStatic>();
        foreach (var s in old.Statics) oldStatics[s.Name] = s;
        foreach (var s in t.Statics)
        {
            ReloadStatic? os;
            if (oldStatics.TryGetValue(s.Name, out os))
            {
                if (os.Type != s.Type) reasons.Add("static type changed: " + t.Name + "." + s.Name);
                else if (os.InitHash != s.InitHash) reasons.Add("static initializer changed: " + t.Name + "." + s.Name);
            }
            else if (!migratesInstances && !s.Pure)
            {
                reasons.Add("impure new static: " + t.Name + "." + s.Name);
            }
        }
    }

    // live に当てられるが、host に知らせたい変化。生きている instance は field の今の値を保つので、
    // instance の field の初期化子を変えても、生きている instance には入らない (新しく作る instance から入る)
    public static List<string> Notes(List<ReloadType> v1, List<ReloadType> v2)
    {
        var notes = new List<string>();
        var next = new Dictionary<string, ReloadType>();
        foreach (var t in v2) next[t.Name] = t;
        foreach (var old in v1)
        {
            ReloadType? t;
            if (!next.TryGetValue(old.Name, out t)) continue;
            var oldFields = new Dictionary<string, ReloadStatic>();
            foreach (var f in old.InstanceFields) oldFields[f.Name] = f;
            foreach (var f in t.InstanceFields)
            {
                ReloadStatic? of;
                if (oldFields.TryGetValue(f.Name, out of) && of.Type == f.Type && of.InitHash != f.InitHash)
                    notes.Add("instance field initializer changed: " + t.Name + "." + f.Name);
            }
        }
        return notes;
    }

    // 保存した enum の値 (int) は読み替えられない。member の追加だけが live
    private static void EnumReasons(ReloadType old, ReloadType t, List<string> reasons)
    {
        var members = new Dictionary<string, int>();
        for (int i = 0; i < t.EnumMemberNames.Count; i++) members[t.EnumMemberNames[i]] = t.EnumMemberValues[i];
        for (int i = 0; i < old.EnumMemberNames.Count; i++)
        {
            int v;
            if (!members.TryGetValue(old.EnumMemberNames[i], out v) || v != old.EnumMemberValues[i])
                reasons.Add("enum member removed or renumbered: " + t.Name + "." + old.EnumMemberNames[i]);
        }
    }

    // reload chunk: IL の metadata の型 (--ref の class は host の table、interface は Lua を出さないので除く)
    public static List<ReloadType> TypesOf(ExportResult export)
    {
        var types = new List<ReloadType>();
        foreach (var c in export.Classes)
        {
            if (c.IsExternal || c.IsInterface) continue;
            var t = new ReloadType(c.Name, c.IsRecord ? "record" : "class");
            t.BaseName = c.BaseName != null ? c.BaseName : "";
            foreach (var f in c.Fields)
            {
                if (f.IsStatic) t.Statics.Add(new ReloadStatic(f.Name, f.Type, f.InitHash, true));
                else t.InstanceFields.Add(new ReloadStatic(f.Name, f.Type, f.InitHash, true));
            }
            types.Add(t);
        }
        foreach (var s in export.Structs)
        {
            var t = new ReloadType(s.Name, "struct");
            foreach (var f in s.StaticFields) t.Statics.Add(new ReloadStatic(f.Name, f.Type, f.InitHash, true));
            types.Add(t);
        }
        foreach (var e in export.EnumTypes)
        {
            var t = new ReloadType(e.Name, "enum");
            foreach (var n in e.MemberNames) t.EnumMemberNames.Add(n);
            foreach (var v in e.MemberValues) t.EnumMemberValues.Add(v);
            types.Add(t);
        }
        return types;
    }
}

// hot reload: 動いている VM の v1 の状態へ v2 を当てる reload chunk を作る (il-design §6、eager な移行)。
//   - class の table は中身を差し替えて identity を保つ (method と static)。struct と enum の型の table も旧い table の中身を v2 で置き換える
//   - 生きている instance は __tcs_instances (weak な表) をたどり、足した field は初期化子、消した field は破棄、残した field は保つ。
//     同じ名前で型が変わった field は新しい型の default に戻す
//   - struct の値は参照の identity を持たないので作り直して移す (layout の変わった struct を型に持つ field を、持ち主から新しい layout の table に組み直す)
//   - 移した後、OnReload があれば instance ごとに 1 回呼ぶ
// 前提: v1 は instance registry 付きで変換して実行し、返す chunk は同じ VM で 1 つの chunk として実行する
public static class ReloadChunk
{
    // v2Lua は v2 を instance registry 付き、型の local を置かない形で変換したもの
    public static string Emit(ExportResult oldExport, ExportResult newExport, string v2Lua, bool traced)
    {
        var ctx = new ReloadContext(oldExport, newExport);
        var lines = new List<string>();
        lines.Add("-- TinyC# hot reload chunk (v2 定義 + eager migration)");
        // registry の無い v1 では v2 の prelude が空の registry を作り、移行が黙って空回りするので、v2 の定義を実行する前に止める
        lines.Add("assert(__tcs_instances, \"hot reload requires v1 built with --instance-registry\")");
        // v2 を実行する前に旧い table を捕まえる (v2 の chunk は同じ名前の global を新しい table で上書きする)
        lines.Add("local __tcs_reload_old = {");
        foreach (var pair in ctx.OldClasses) lines.Add("  " + pair.Name + " = " + pair.Name + ",");
        foreach (var name in ctx.PlainTables) lines.Add("  " + name + " = " + name + ",");
        lines.Add("}");
        lines.Add(v2Lua);
        lines.Add("do");
        // fresh を捕まえ、global を旧い identity に戻す。以後 method の本文からの global の参照は旧い table に解決される
        lines.Add("  local __tcs_reload_fresh = {");
        foreach (var fresh in ctx.NewClasses) lines.Add("    " + fresh.Name + " = " + fresh.Name + ",");
        lines.Add("  }");
        foreach (var old in ctx.OldClasses) lines.Add("  " + old.Name + " = __tcs_reload_old." + old.Name);
        if (ctx.PlainTables.Count > 0)
        {
            lines.Add("  local function __tcs_reload_replace(old, fresh)");
            lines.Add("    for k in pairs(old) do old[k] = nil end");
            lines.Add("    for k, v in pairs(fresh) do old[k] = v end");
            lines.Add("    return old");
            lines.Add("  end");
            foreach (var name in ctx.PlainTables)
            {
                var retained = RetainedStructStatics(oldExport, newExport, name);
                var keep = new List<string>();
                foreach (var f in retained) keep.Add(f.Name + " = __tcs_reload_old." + name + "." + f.Name);
                if (retained.Count > 0) lines.Add("  local __kept_" + name + " = {" + string.Join(", ", keep) + "}");
                lines.Add("  " + name + " = __tcs_reload_replace(__tcs_reload_old." + name + ", " + name + ")");
                if (retained.Count > 0) lines.Add("  for k, v in pairs(__kept_" + name + ") do " + name + "[k] = v end");
            }
        }
        // --trace: v2 の定義が登録した fresh の型の table を、identity を戻した global (旧い table) に向け直す
        if (traced) lines.Add("  for k in pairs(__tcs_trace_classes) do __tcs_trace_classes[k] = _ENV[k] end");
        EmitStructMigrators(lines, ctx);
        foreach (var st in newExport.Structs)
        {
            if (FindStruct(oldExport, st.Name) == null) continue;
            foreach (var field in RetainedStructStatics(oldExport, newExport, st.Name))
                EmitStructReserialize(lines, "  ", st.Name + "." + field.Name, field.Type, ctx);
        }
        for (int i = 0; i < ctx.OldClasses.Count; i++) EmitClassMerge(lines, ctx.OldClasses[i], ctx.NewClasses[i], ctx);
        EmitInstanceMigration(lines, ctx);
        lines.Add("end");
        return string.Join("\n", lines) + "\n";
    }

    private static ExStruct? FindStruct(ExportResult export, string name)
    {
        foreach (var s in export.Structs)
        {
            if (s.Name == name) return s;
        }
        return null;
    }

    // 型の table を置き換えても残す static (class の retained と同じ)。追加は fresh の初期値
    private static List<ExField> RetainedStructStatics(ExportResult oldExport, ExportResult newExport, string name)
    {
        var result = new List<ExField>();
        var n = FindStruct(newExport, name);
        var o = FindStruct(oldExport, name);
        if (n == null || o == null) return result;
        foreach (var f in n.StaticFields)
        {
            foreach (var of in o.StaticFields)
            {
                if (of.Name == f.Name)
                {
                    result.Add(f);
                    break;
                }
            }
        }
        return result;
    }

    // layout の変わった struct ごとの作り直しの関数。旧い値の残す field を新しい layout の table に写し、足した field は default、struct の field は再帰で移す
    private static void EmitStructMigrators(List<string> lines, ReloadContext ctx)
    {
        if (ctx.ChangedStructs.Count == 0) return;
        foreach (var name in ctx.ChangedStructs) lines.Add("  local __tcs_migrate_" + name);
        foreach (var name in ctx.ChangedStructs)
        {
            var n = ctx.NewStructs[name];
            var oldFields = new Dictionary<string, string>();
            foreach (var f in ctx.OldStructs[name].Fields) oldFields[f.Name] = f.Type;
            lines.Add("  __tcs_migrate_" + name + " = function(v)");
            lines.Add("    if v == nil then return nil end");
            lines.Add("    local n = {}");
            foreach (var field in n.Fields)
            {
                string? oldType;
                bool same = oldFields.TryGetValue(field.Name, out oldType) && oldType == field.Type;
                lines.Add("    n." + field.Name + " = " + MigratedValue("v." + field.Name, field.Type, same, ctx));
            }
            lines.Add("    return n");
            lines.Add("  end");
        }
    }

    // field の値の移し方: 同じ名前で同じ型なら旧い値 (struct なら再帰で移す)、それ以外 (追加、型の変更) は新しい型の default
    private static string MigratedValue(string oldExpr, string type, bool retainedSameType, ReloadContext ctx)
    {
        if (!retainedSameType) return DefaultFor(type, ctx);
        string lua = ctx.LuaName(type);
        if (ctx.ChangedStructs.Contains(lua)) return "__tcs_migrate_" + lua + "(" + oldExpr + ")";
        return oldExpr;
    }

    private static void EmitClassMerge(List<string> lines, ExClass old, ExClass n, ReloadContext ctx)
    {
        lines.Add("  do -- " + n.Name + ": method / static merge");
        lines.Add("    local __old = __tcs_reload_old." + n.Name);
        lines.Add("    local __fresh = __tcs_reload_fresh." + n.Name);
        // method は全て差し替える (accessor、演算子の metamethod、new を含む)。__index は旧い table の自己参照のまま
        var newMethods = MethodAndCtorNames(n);
        if (!newMethods.Contains("new")) newMethods.Add("new");
        foreach (var name in newMethods) lines.Add("    __old." + name + " = __fresh." + name);
        foreach (var removed in MethodAndCtorNames(old))
        {
            if (!newMethods.Contains(removed)) lines.Add("    __old." + removed + " = nil");
        }
        // static: 残すものは今の値を保ち (struct の型は作り直す)、足したものは fresh の初期値、消したものは破棄。型の変更は restart (ReloadRules)
        var oldStatics = new List<string>();
        foreach (var f in old.Fields)
        {
            if (f.IsStatic && !oldStatics.Contains(f.Name)) oldStatics.Add(f.Name);
        }
        var newStatics = new List<string>();
        foreach (var field in n.Fields)
        {
            if (!field.IsStatic) continue;
            newStatics.Add(field.Name);
            if (!oldStatics.Contains(field.Name)) lines.Add("    __old." + field.Name + " = __fresh." + field.Name);
            else EmitStructReserialize(lines, "    ", "__old." + field.Name, field.Type, ctx);
        }
        foreach (var removed in oldStatics)
        {
            if (!newStatics.Contains(removed)) lines.Add("    __old." + removed + " = nil");
        }
        lines.Add("  end");
    }

    // 重ならない順の method と constructor の名前
    private static List<string> MethodAndCtorNames(ExClass c)
    {
        var names = new List<string>();
        foreach (var m in c.Methods)
        {
            if (!names.Contains(m.Name)) names.Add(m.Name);
        }
        foreach (var k in c.Ctors)
        {
            if (!names.Contains(k.Name)) names.Add(k.Name);
        }
        return names;
    }

    // target が layout の変わった struct (そのものか配列) なら作り直しの文を出す
    private static void EmitStructReserialize(List<string> lines, string indent, string target, string type, ReloadContext ctx)
    {
        string lua = ctx.LuaName(type);
        if (ctx.ChangedStructs.Contains(lua))
        {
            lines.Add(indent + target + " = __tcs_migrate_" + lua + "(" + target + ")");
            return;
        }
        if (!type.EndsWith("[]")) return;
        string elem = ctx.LuaName(Strings.Prefix(type, Strings.ByteLength(type) - 2));
        if (!ctx.ChangedStructs.Contains(elem)) return;
        lines.Add(indent + "do");
        lines.Add(indent + "  local __a = " + target);
        lines.Add(indent + "  if __a ~= nil then");
        lines.Add(indent + "    for __i = 1, #__a do");
        lines.Add(indent + "      __a[__i] = __tcs_migrate_" + elem + "(__a[__i])");
        lines.Add(indent + "    end");
        lines.Add(indent + "  end");
        lines.Add(indent + "end");
    }

    private static void EmitInstanceMigration(List<string> lines, ReloadContext ctx)
    {
        // instance の class の鎖 (作った class → 基底 ...) に対象の class があるか。継承した field の移行を派生の instance にも届かせる
        lines.Add("  local function __tcs_chain_has(cls, target)");
        lines.Add("    local c = cls");
        lines.Add("    while c do");
        lines.Add("      if c == target then return true end");
        lines.Add("      local link = getmetatable(c)");
        lines.Add("      c = link and link.__index");
        lines.Add("    end");
        lines.Add("    return false");
        lines.Add("  end");
        lines.Add("  local __tcs_touched = {}");
        lines.Add("  for __inst, __cls in pairs(__tcs_instances) do");
        for (int i = 0; i < ctx.OldClasses.Count; i++)
        {
            var old = ctx.OldClasses[i];
            var n = ctx.NewClasses[i];
            var body = new List<string>();
            var oldFields = new Dictionary<string, string>();
            var oldOrder = new List<string>();
            foreach (var f in old.Fields)
            {
                if (f.IsStatic || oldFields.ContainsKey(f.Name)) continue;
                oldFields[f.Name] = f.Type;
                oldOrder.Add(f.Name);
            }
            var newNames = new List<string>();
            foreach (var field in n.Fields)
            {
                if (field.IsStatic) continue;
                newNames.Add(field.Name);
                string? oldType;
                bool same = oldFields.TryGetValue(field.Name, out oldType) && oldType == field.Type;
                if (!same)
                {
                    // 足した field と型の変わった field: 初期化子、無ければ新しい型の default
                    var init = field.Init;
                    string value = init != null ? new LuaGen().RenderIl(init) : DefaultFor(field.Type, ctx);
                    body.Add("      __inst." + field.Name + " = " + value);
                }
                else EmitStructReserialize(body, "      ", "__inst." + field.Name, field.Type, ctx);
            }
            foreach (var name in oldOrder)
            {
                if (!newNames.Contains(name)) body.Add("      __inst." + name + " = nil");
            }
            if (body.Count == 0) continue;
            lines.Add("    if __tcs_chain_has(__cls, __tcs_reload_old." + old.Name + ") then");
            foreach (var b in body) lines.Add(b);
            lines.Add("      __tcs_touched[__inst] = true");
            lines.Add("    end");
        }
        // OnReload は field の移行の後、instance ごとに 1 回 (鎖の上のどれかの class が reload の対象なら対象)
        lines.Add("    local __hit = false");
        foreach (var old in ctx.OldClasses) lines.Add("    __hit = __hit or __tcs_chain_has(__cls, __tcs_reload_old." + old.Name + ")");
        lines.Add("    if __hit then __tcs_touched[__inst] = true end");
        lines.Add("  end");
        lines.Add("  for __inst in pairs(__tcs_touched) do");
        lines.Add("    if __inst.OnReload then __inst:OnReload() end");
        lines.Add("  end");
    }

    // 新しい型の default の値。struct は v2 の zero 値 (global は v2 の fresh に解決されるので新しい layout で作る)、enum は 0
    private static string DefaultFor(string type, ReloadContext ctx)
    {
        string lua = ctx.LuaName(type);
        if (ctx.NewStructs.ContainsKey(lua)) return lua + ".new()";
        if (ctx.NewEnums.Contains(lua)) return "0";
        if (lua == "int" || lua == "long" || lua == "uint" || lua == "float" || lua == "double") return "0";
        if (lua == "bool") return "false";
        return "nil";
    }
}

public sealed class ReloadContext
{
    // v1 と v2 の両方にある class (同じ順)
    public List<ExClass> OldClasses = new List<ExClass>();
    public List<ExClass> NewClasses = new List<ExClass>();
    public Dictionary<string, ExStruct> OldStructs = new Dictionary<string, ExStruct>();
    public Dictionary<string, ExStruct> NewStructs = new Dictionary<string, ExStruct>();
    public HashSet<string> NewEnums = new HashSet<string>();
    // layout の変わった struct (v2 の順)
    public List<string> ChangedStructs = new List<string>();
    // v1 と v2 の両方にある struct と enum の型の table (instance からは参照されないが、v1 の chunk の closure が旧い table を引く)
    public List<string> PlainTables = new List<string>();
    // IL の型の文字列は C# の表示名 (`A.Vec`)。Lua の global の名前 (`A_Vec`) に写す
    private Dictionary<string, string> _aliases = new Dictionary<string, string>();

    public ReloadContext(ExportResult oldExport, ExportResult newExport)
    {
        var oldByName = new Dictionary<string, ExClass>();
        foreach (var c in oldExport.Classes)
        {
            // --ref の class (IsExternal) は host の table で、Lua の global ではない
            if (!c.IsExternal) oldByName[c.Name] = c;
        }
        foreach (var c in newExport.Classes)
        {
            ExClass? old;
            if (c.IsExternal || !oldByName.TryGetValue(c.Name, out old)) continue;
            OldClasses.Add(old);
            NewClasses.Add(c);
        }
        foreach (var s in oldExport.Structs) OldStructs[s.Name] = s;
        foreach (var s in newExport.Structs)
        {
            NewStructs[s.Name] = s;
            ExStruct? old;
            if (OldStructs.TryGetValue(s.Name, out old) && old.LayoutHash != s.LayoutHash) ChangedStructs.Add(s.Name);
        }
        foreach (var e in newExport.Enums) NewEnums.Add(e);
        foreach (var s in newExport.Structs) AddAlias(s.DisplayName, s.Name);
        foreach (var e in newExport.EnumTypes) AddAlias(e.DisplayName, e.Name);
        var newPlain = PlainTypeNames(newExport);
        foreach (var name in PlainTypeNames(oldExport))
        {
            if (newPlain.Contains(name) && !PlainTables.Contains(name)) PlainTables.Add(name);
        }
    }

    private void AddAlias(string? display, string name)
    {
        if (display != null && display != name) _aliases[display] = name;
    }

    private static List<string> PlainTypeNames(ExportResult export)
    {
        var names = new List<string>();
        foreach (var s in export.Structs) names.Add(s.Name);
        foreach (var e in export.Enums) names.Add(e);
        return names;
    }

    public string LuaName(string type)
    {
        string? name;
        if (_aliases.TryGetValue(type, out name)) return name;
        return type;
    }
}
