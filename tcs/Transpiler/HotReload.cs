using System.Text;

namespace TinyCs;

// hot reload の instance と struct の値の移行 (il-design §6: eager migration)。
// ModuleRegistry の batch が、型 table を当て直した後に走らせる Lua を組む。規則:
//   - 生存インスタンスは __tcs_instances (weak registry) を walk して
//     added=initializer / discarded=破棄 / retained=保持 を適用。
//     同名で型が変わった field は新型の default へ reset する
//   - struct 値は参照 identity を持たないため再直列化で移行する:
//     layout が変わった struct を型に持つ field (直接 / 配列、static を含む) を
//     owner 経由で新 layout の table に組み直す (struct in struct は再帰)
//   - reload は frame 境界で行う前提 (実行中 frame の local は移行対象外)
// 前提: instance は instance registry 付き (Transpile / session の instanceRegistry) で
// 作られている。List<T> / Dictionary<K,V> 内の struct 値の再直列化は未対応 (需要待ち)。
public static class HotReload
{
    private sealed record Context(
        List<(IlClassInfo Old, IlClassInfo New)> Classes,
        Dictionary<string, IlStructInfo> NewStructs,
        HashSet<string> NewEnums,
        HashSet<string> ChangedStructs,
        LuaEmitter Emitter,
        Dictionary<string, string> TypeAliases)
    {
        // IL の型文字列は C# 表示名 (`A.Vec`)。Lua global 名 (`A_Vec`) へ写す
        public string LuaName(string type) =>
            TypeAliases.GetValueOrDefault(type, type);
    }

    // ModuleRegistry の batch で、型 table を当て直した後 (define と static の初期化の後) に
    // 走らせる移行の Lua (instance の field、struct の値)。registry は型 table の identity を
    // 保つので、旧い table も新しい table も型の名前で引ける (module env)。
    // 移すものが無ければ null
    public static string? EmitRegistryMigration(IlExportResult oldExport, IlExportResult newExport)
    {
        var (ctx, oldStructs) = BuildContext(oldExport, newExport);
        var sb = new StringBuilder();
        EmitStructMigrators(sb, ctx, oldStructs);
        foreach (var (old, @new) in ctx.Classes)
        {
            var oldStatics = old.Fields.Where(f => f.IsStatic).Select(f => f.Name).ToHashSet();
            foreach (var field in @new.Fields.Where(f => f.IsStatic && oldStatics.Contains(f.Name)))
                EmitStructReserialize(sb, "  ", $"{@new.Name}.{field.Name}", field.Type, ctx);
        }
        var instances = new StringBuilder();
        EmitInstanceMigration(instances, ctx);
        return sb.Length == 0 && instances.Length == 0 ? null : sb.Append(instances).ToString();
    }

    private static (Context Ctx, Dictionary<string, IlStructInfo> OldStructs) BuildContext(
        IlExportResult oldExport, IlExportResult newExport)
    {
        // --ref の class (IsExternal) は host の table で、Lua の global ではない
        var oldByName = oldExport.Classes.Where(c => !c.IsExternal)
            .ToDictionary(c => c.Name);
        var pairs = newExport.Classes
            .Where(c => !c.IsExternal && oldByName.ContainsKey(c.Name))
            .Select(c => (Old: oldByName[c.Name], New: c))
            .ToList();

        var oldStructs = oldExport.Structs.ToDictionary(s => s.Name);
        var newStructs = newExport.Structs.ToDictionary(s => s.Name);
        var changedStructs = newStructs.Values
            .Where(s => oldStructs.TryGetValue(s.Name, out var old)
                && old.LayoutHash != s.LayoutHash)
            .Select(s => s.Name)
            .ToHashSet();
        var ctx = new Context(pairs, newStructs,
            newExport.Enums.IsDefault ? [] : [.. newExport.Enums],
            changedStructs, new LuaEmitter(),
            newExport.Structs.Select(s => (s.DisplayName, s.Name))
                .Concat(newExport.EnumTypes.Select(e => (e.DisplayName, e.Name)))
                .Where(x => x.DisplayName != null && x.DisplayName != x.Name)
                .ToDictionary(x => x.DisplayName!, x => x.Name));
        return (ctx, oldStructs);
    }

    // layout の変わった struct ごとの再直列化関数。旧値の retained field を
    // 新 layout の table へ写し、added は default (struct なら zero 値)、
    // struct field は再帰的に migrate する
    private static void EmitStructMigrators(StringBuilder sb, Context ctx,
        Dictionary<string, IlStructInfo> oldStructs)
    {
        if (ctx.ChangedStructs.Count == 0)
            return;
        foreach (var name in ctx.ChangedStructs)
            sb.AppendLine($"  local __tcs_migrate_{name}");
        foreach (var name in ctx.ChangedStructs)
        {
            var @new = ctx.NewStructs[name];
            var oldFields = oldStructs[name].Fields
                .ToDictionary(f => f.Name, f => f.Type);
            sb.AppendLine($"  __tcs_migrate_{name} = function(v)");
            sb.AppendLine("    if v == nil then return nil end");
            sb.AppendLine("    local n = {}");
            foreach (var field in @new.Fields)
            {
                var retainedSameType = oldFields.TryGetValue(field.Name,
                    out var oldType) && oldType == field.Type;
                sb.AppendLine($"    n.{field.Name} = "
                    + MigratedValue($"v.{field.Name}", field.Type,
                        retainedSameType, ctx));
            }
            sb.AppendLine("    return n");
            sb.AppendLine("  end");
        }
    }

    // field 値の移行式: retained (同名同型) は旧値 (struct なら再帰 migrate)、
    // それ以外 (added / 型変更) は新型の default
    private static string MigratedValue(string oldExpr, string type,
        bool retainedSameType, Context ctx)
    {
        if (!retainedSameType)
            return DefaultFor(type, ctx);
        if (ctx.ChangedStructs.Contains(ctx.LuaName(type)))
            return $"__tcs_migrate_{ctx.LuaName(type)}({oldExpr})";
        return oldExpr;
    }

    // target が変更 struct (直接 / 配列) なら再直列化文を出す
    private static void EmitStructReserialize(StringBuilder sb, string indent,
        string target, string type, Context ctx)
    {
        if (ctx.ChangedStructs.Contains(ctx.LuaName(type)))
        {
            sb.AppendLine($"{indent}{target} = __tcs_migrate_{ctx.LuaName(type)}({target})");
        }
        else if (type.EndsWith("[]")
            && ctx.ChangedStructs.Contains(ctx.LuaName(type[..^2])))
        {
            var elem = ctx.LuaName(type[..^2]);
            sb.AppendLine($"{indent}do");
            sb.AppendLine($"{indent}  local __a = {target}");
            sb.AppendLine($"{indent}  if __a ~= nil then");
            sb.AppendLine($"{indent}    for __i = 1, #__a do");
            sb.AppendLine($"{indent}      __a[__i] = __tcs_migrate_{elem}(__a[__i])");
            sb.AppendLine($"{indent}    end");
            sb.AppendLine($"{indent}  end");
            sb.AppendLine($"{indent}end");
        }
    }

    // registry は型 table の identity を保つので、旧い class table も名前で引ける
    private static void EmitInstanceMigration(StringBuilder sb, Context ctx)
    {
        var classes = new StringBuilder();
        // instance の class chain (構築時 class → base...) に対象 class が
        // 含まれるか。継承 field の migration を派生 instance にも届かせる
        foreach (var (old, @new) in ctx.Classes)
        {
            var body = new StringBuilder();
            var oldFields = old.Fields.Where(f => !f.IsStatic)
                .ToDictionary(f => f.Name, f => f.Type);
            var newFields = @new.Fields.Where(f => !f.IsStatic).ToList();
            foreach (var field in newFields)
            {
                var retainedSameType = oldFields.TryGetValue(field.Name,
                    out var oldType) && oldType == field.Type;
                if (!retainedSameType)
                {
                    // added / 型変更: initializer render、無ければ新型 default
                    var init = field.Init != null
                        ? ctx.Emitter.RenderIlExpr(field.Init)
                        : DefaultFor(field.Type, ctx);
                    body.AppendLine($"      __inst.{field.Name} = {init}");
                }
                else
                {
                    EmitStructReserialize(body, "      ",
                        $"__inst.{field.Name}", field.Type, ctx);
                }
            }
            foreach (var name in oldFields.Keys
                .Except(newFields.Select(f => f.Name)))
                body.AppendLine($"      __inst.{name} = nil");
            if (body.Length == 0)
                continue;

            classes.AppendLine("    if __tcs_chain_has(__cls, "
                + $"{old.Name}) then");
            classes.Append(body);
            classes.AppendLine("    end");
        }
        if (classes.Length == 0)
            return;
        sb.AppendLine("  local function __tcs_chain_has(cls, target)");
        sb.AppendLine("    local c = cls");
        sb.AppendLine("    while c do");
        sb.AppendLine("      if c == target then return true end");
        sb.AppendLine("      local link = getmetatable(c)");
        sb.AppendLine("      c = link and link.__index");
        sb.AppendLine("    end");
        sb.AppendLine("    return false");
        sb.AppendLine("  end");
        sb.AppendLine("  for __inst, __cls in pairs(__tcs_instances) do");
        sb.Append(classes);
        sb.AppendLine("  end");
    }

    // 新型の default 値。struct は v2 の zero 値 (型 table は当て直した後なので新 layout で
    // 構築される)、enum は 0 (default(E))
    private static string DefaultFor(string type, Context ctx)
    {
        type = ctx.LuaName(type);
        if (ctx.NewStructs.ContainsKey(type))
            return $"{type}.new()";
        if (ctx.NewEnums.Contains(type))
            return "0";
        return type switch
        {
            "int" or "long" or "uint" or "float" or "double" => "0",
            "bool" => "false",
            _ => "nil",
        };
    }
}
