using System.Text;

namespace TinyCs;

// T220(b): hot reload — 実行中 VM の v1 状態へ v2 を適用する reload chunk を
// 生成する (il-design §6: eager migration)。適用規則:
//   - class table は in-place 更新で identity を保つ (method / static の差し替え)
//   - 生存インスタンスは __tcs_instances (weak registry) を walk して
//     added=initializer / discarded=破棄 / retained=保持 を適用
//   - migration 完了後、OnReload メソッドがあれば instance ごとに 1 回呼ぶ
//   - reload は frame 境界で行う前提 (実行中 frame の local は移行対象外)
// 前提: v1 chunk を実行済みの同一 VM で、返り値の chunk を 1 つの chunk として
// 実行する。record class は IlExport 対象外のため現時点では移行されない。
public static class HotReload
{
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

        var emitter = new LuaEmitter();
        var sb = new StringBuilder();
        sb.AppendLine("-- TinyC# hot reload chunk (v2 定義 + eager migration)");

        // v2 実行前に旧 class table を捕まえる (v2 chunk は同名 global を
        // 新しい table で上書きするため)
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

        foreach (var (old, @new) in pairs)
            EmitClassMerge(sb, old, @new);

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

        EmitInstanceMigration(sb, pairs, emitter);
        sb.AppendLine("end");
        return sb.ToString();
    }

    private static void EmitClassMerge(StringBuilder sb,
        IlClassInfo old, IlClassInfo @new)
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

        // static: retained は生存値を保持、added は fresh の初期値、
        // discarded は破棄
        var oldStatics = old.Fields.Where(f => f.IsStatic)
            .Select(f => f.Name).ToHashSet();
        var newStatics = @new.Fields.Where(f => f.IsStatic)
            .Select(f => f.Name).ToHashSet();
        foreach (var added in newStatics.Except(oldStatics))
            sb.AppendLine($"    __old.{added} = __fresh.{added}");
        foreach (var removed in oldStatics.Except(newStatics))
            sb.AppendLine($"    __old.{removed} = nil");
        sb.AppendLine("  end");
    }

    private static void EmitInstanceMigration(StringBuilder sb,
        List<(IlClassInfo Old, IlClassInfo New)> pairs, LuaEmitter emitter)
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
        foreach (var (old, @new) in pairs)
        {
            var oldFields = old.Fields.Where(f => !f.IsStatic)
                .ToDictionary(f => f.Name);
            var newFields = @new.Fields.Where(f => !f.IsStatic).ToList();
            var added = newFields.Where(f => !oldFields.ContainsKey(f.Name))
                .ToList();
            var discarded = oldFields.Keys
                .Except(newFields.Select(f => f.Name)).ToList();
            if (added.Count == 0 && discarded.Count == 0)
                continue;

            sb.AppendLine("    if __tcs_chain_has(__cls, "
                + $"__tcs_reload_old.{old.Name}) then");
            foreach (var field in added)
            {
                var init = field.Init != null
                    ? emitter.RenderIlExpr(field.Init)
                    : DefaultFor(field.Type);
                sb.AppendLine($"      __inst.{field.Name} = {init}");
            }
            foreach (var name in discarded)
                sb.AppendLine($"      __inst.{name} = nil");
            sb.AppendLine("      __tcs_touched[__inst] = true");
            sb.AppendLine("    end");
        }
        // OnReload は field migration 完了後、instance ごとに 1 回
        // (chain 上のいずれかの class が reload 対象なら対象)
        sb.AppendLine("    local __hit = false");
        foreach (var (old, _) in pairs)
            sb.AppendLine("    __hit = __hit or __tcs_chain_has(__cls, "
                + $"__tcs_reload_old.{old.Name})");
        sb.AppendLine("    if __hit then __tcs_touched[__inst] = true end");
        sb.AppendLine("  end");
        sb.AppendLine("  for __inst in pairs(__tcs_touched) do");
        sb.AppendLine("    if __inst.OnReload then __inst:OnReload() end");
        sb.AppendLine("  end");
    }

    private static string DefaultFor(string type) => type switch
    {
        "int" or "long" or "uint" or "float" or "double" => "0",
        "bool" => "false",
        _ => "nil",
    };
}
