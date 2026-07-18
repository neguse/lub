using System.Text;

namespace TinyCs;

// T220(b)(c): hot reload — 実行中 VM の v1 状態へ v2 を適用する reload chunk を
// 生成する (il-design §6: eager migration)。適用規則:
//   - class table は in-place 更新で identity を保つ (method / static の差し替え)
//   - 生存インスタンスは __tcs_instances (weak registry) を walk して
//     added=initializer / discarded=破棄 / retained=保持 を適用。
//     同名で型が変わった field は新型の default へ reset する
//   - struct 値は参照 identity を持たないため再直列化で移行する:
//     layout が変わった struct を型に持つ field (直接 / 配列) を owner 経由で
//     新 layout の table に組み直す (struct in struct は再帰)
//   - migration 完了後、OnReload メソッドがあれば instance ごとに 1 回呼ぶ
//   - reload は frame 境界で行う前提 (実行中 frame の local は移行対象外)
// 前提: v1 chunk を実行済みの同一 VM で、返り値の chunk を 1 つの chunk として
// 実行する。record class は IlExport 対象外のため現時点では移行されない。
// List<T> / Dictionary<K,V> 内の struct 値の再直列化は未対応 (需要待ち)。
public static class HotReload
{
    private sealed record Context(
        List<(IlClassInfo Old, IlClassInfo New)> Classes,
        Dictionary<string, IlStructInfo> NewStructs,
        HashSet<string> ChangedStructs,
        LuaEmitter Emitter);

    public static string EmitReloadChunk(string[] v1Sources, string[] v2Sources)
    {
        var oldExport = IlExport.Export(v1Sources);
        var newExport = IlExport.Export(v2Sources);
        var v2Lua = Transpiler.Transpile(v2Sources);

        var oldByName = oldExport.Classes.ToDictionary(c => c.Name);
        var pairs = newExport.Classes
            .Where(c => oldByName.ContainsKey(c.Name))
            .Select(c => (Old: oldByName[c.Name], New: c))
            .ToList();

        var oldStructs = oldExport.Structs.ToDictionary(s => s.Name);
        var newStructs = newExport.Structs.ToDictionary(s => s.Name);
        var changedStructs = newStructs.Values
            .Where(s => oldStructs.TryGetValue(s.Name, out var old)
                && old.LayoutHash != s.LayoutHash)
            .Select(s => s.Name)
            .ToHashSet();
        var ctx = new Context(pairs, newStructs, changedStructs,
            new LuaEmitter());

        var sb = new StringBuilder();
        sb.AppendLine("-- TinyC# hot reload chunk (v2 定義 + eager migration)");

        // v2 実行前に旧 class table を捕まえる (v2 chunk は同名 global を
        // 新しい table で上書きするため)。struct の型 table は instance から
        // 参照されない (metatable なし) ので捕獲不要 — fresh をそのまま使う
        sb.AppendLine("local __tcs_reload_old = {");
        foreach (var (old, _) in pairs)
            sb.AppendLine($"  {old.Name} = {old.Name},");
        sb.AppendLine("}");

        sb.AppendLine(v2Lua);

        sb.AppendLine("do");
        // fresh を捕まえ、global を旧 identity へ戻す。以後 method 本文からの
        // global 参照 (静的 field / base.new) はすべて旧 table に解決される
        sb.AppendLine("  local __tcs_reload_fresh = {");
        foreach (var (_, fresh) in pairs)
            sb.AppendLine($"    {fresh.Name} = {fresh.Name},");
        sb.AppendLine("  }");
        foreach (var (old, _) in pairs)
            sb.AppendLine($"  {old.Name} = __tcs_reload_old.{old.Name}");

        EmitStructMigrators(sb, ctx, oldStructs);

        foreach (var pair in pairs)
            EmitClassMerge(sb, pair.Old, pair.New, ctx);

        // 親の付け替え (v2 で base が変わった場合)。global 復元後なので
        // base 名は旧 identity (または v2 新設 class) に解決される
        foreach (var (old, @new) in pairs)
        {
            if (old.BaseName == @new.BaseName)
                continue;
            sb.AppendLine(@new.BaseName != null
                ? $"  setmetatable({@new.Name}, {{ __index = {@new.BaseName} }})"
                : $"  setmetatable({@new.Name}, nil)");
        }

        EmitInstanceMigration(sb, ctx);
        sb.AppendLine("end");
        return sb.ToString();
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
        if (ctx.ChangedStructs.Contains(type))
            return $"__tcs_migrate_{type}({oldExpr})";
        return oldExpr;
    }

    private static void EmitClassMerge(StringBuilder sb,
        IlClassInfo old, IlClassInfo @new, Context ctx)
    {
        sb.AppendLine($"  do -- {@new.Name}: method / static merge");
        sb.AppendLine($"    local __old = __tcs_reload_old.{@new.Name}");
        sb.AppendLine($"    local __fresh = __tcs_reload_fresh.{@new.Name}");

        // method は全面差し替え (accessor / operator metamethod / new を含む)。
        // __index は旧 table の自己参照のまま維持する
        var newMethods = @new.Methods.Select(m => m.Name)
            .Append("new").Distinct().ToList();
        foreach (var name in newMethods)
            sb.AppendLine($"    __old.{name} = __fresh.{name}");
        foreach (var removed in old.Methods.Select(m => m.Name)
            .Except(@new.Methods.Select(m => m.Name)))
            sb.AppendLine($"    __old.{removed} = nil");

        // static: retained (同名同型) は生存値を保持 (struct 型は再直列化)、
        // added / 型変更は fresh の初期値、discarded は破棄
        var oldStatics = old.Fields.Where(f => f.IsStatic)
            .ToDictionary(f => f.Name, f => f.Type);
        var newStatics = @new.Fields.Where(f => f.IsStatic).ToList();
        foreach (var field in newStatics)
        {
            var retainedSameType = oldStatics.TryGetValue(field.Name,
                out var oldType) && oldType == field.Type;
            if (!retainedSameType)
                sb.AppendLine($"    __old.{field.Name} = __fresh.{field.Name}");
            else
                EmitStructReserialize(sb, "    ", $"__old.{field.Name}",
                    field.Type, ctx);
        }
        foreach (var removed in oldStatics.Keys
            .Except(newStatics.Select(f => f.Name)))
            sb.AppendLine($"    __old.{removed} = nil");
        sb.AppendLine("  end");
    }

    // target が変更 struct (直接 / 配列) なら再直列化文を出す
    private static void EmitStructReserialize(StringBuilder sb, string indent,
        string target, string type, Context ctx)
    {
        if (ctx.ChangedStructs.Contains(type))
        {
            sb.AppendLine($"{indent}{target} = __tcs_migrate_{type}({target})");
        }
        else if (type.EndsWith("[]")
            && ctx.ChangedStructs.Contains(type[..^2]))
        {
            var elem = type[..^2];
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

    private static void EmitInstanceMigration(StringBuilder sb, Context ctx)
    {
        // instance の class chain (構築時 class → base...) に対象 class が
        // 含まれるか。継承 field の migration を派生 instance にも届かせる
        sb.AppendLine("  local function __tcs_chain_has(cls, target)");
        sb.AppendLine("    local c = cls");
        sb.AppendLine("    while c do");
        sb.AppendLine("      if c == target then return true end");
        sb.AppendLine("      local link = getmetatable(c)");
        sb.AppendLine("      c = link and link.__index");
        sb.AppendLine("    end");
        sb.AppendLine("    return false");
        sb.AppendLine("  end");
        sb.AppendLine("  local __tcs_touched = {}");
        sb.AppendLine("  for __inst, __cls in pairs(__tcs_instances) do");
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

            sb.AppendLine("    if __tcs_chain_has(__cls, "
                + $"__tcs_reload_old.{old.Name}) then");
            sb.Append(body);
            sb.AppendLine("      __tcs_touched[__inst] = true");
            sb.AppendLine("    end");
        }
        // OnReload は field migration 完了後、instance ごとに 1 回
        // (chain 上のいずれかの class が reload 対象なら対象)
        sb.AppendLine("    local __hit = false");
        foreach (var (old, _) in ctx.Classes)
            sb.AppendLine("    __hit = __hit or __tcs_chain_has(__cls, "
                + $"__tcs_reload_old.{old.Name})");
        sb.AppendLine("    if __hit then __tcs_touched[__inst] = true end");
        sb.AppendLine("  end");
        sb.AppendLine("  for __inst in pairs(__tcs_touched) do");
        sb.AppendLine("    if __inst.OnReload then __inst:OnReload() end");
        sb.AppendLine("  end");
    }

    // 新型の default 値。struct は v2 の zero 値 (global は v2 fresh に
    // 解決されるので新 layout で構築される)
    private static string DefaultFor(string type, Context ctx)
    {
        if (ctx.NewStructs.ContainsKey(type))
            return $"{type}.new()";
        return type switch
        {
            "int" or "long" or "uint" or "float" or "double" => "0",
            "bool" => "false",
            _ => "nil",
        };
    }
}
