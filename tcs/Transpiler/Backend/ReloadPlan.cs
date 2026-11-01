using System.Collections.Immutable;

namespace TinyCs.Backend;

/// <summary>reload の計画 (doc/object-model.md §6)。旧版と新版の型記述子 (<see cref="TypeTable"/>) から、
/// 型ごとの retained / added / reset、static の差分、restart の理由を求め、byte 列 (v1) にする。
/// VM の tvm_apply と tcs2c の migrator が同じ構造で受ける (C の runtime/model/tcs_walk が読む)。</summary>
public static class ReloadPlan
{
    public const byte Same = 0, Added = 1, Removed = 2, Relayout = 3;
    public const byte FieldRetained = 0, FieldAdded = 1, FieldReset = 2;

    public sealed record FieldPlan(byte Source, ushort OldField, uint Init);

    public sealed record TypePlan(string Name, byte Change, ImmutableArray<FieldPlan> Fields);

    public sealed record StaticPlan(string Owner, string Field, byte Change);

    public sealed record Plan(ImmutableArray<string> Reasons, ImmutableArray<TypePlan> Types, ImmutableArray<StaticPlan> Statics);

    /// <summary>型を名前で対応させる。field は名前で対応させ、種類と型 (の名前) が同じなら retained、違えば reset
    /// (hot-reload §5.2: 新しい型の 0 の値)、新しい名前は added。struct の field は、その struct の形が変わった
    /// とき owner も relayout になる (layout hash が展開して伝わるのと同じ)。</summary>
    public static Plan Compute(IReadOnlyList<TypeTable.Entry> oldTypes, IReadOnlyList<TypeTable.Entry> newTypes,
        IEnumerable<(string Owner, string Field)> oldStatics, IEnumerable<(string Owner, string Field)> newStatics,
        IEnumerable<string>? restartReasons = null,
        IReadOnlyDictionary<(string Type, string Field), int>? initializers = null)
    {
        var reasons = new List<string>(restartReasons ?? []);
        var oldByName = oldTypes.Select((t, i) => (t, i)).GroupBy(x => x.t.Name).ToDictionary(g => g.Key, g => g.First());
        var newByName = newTypes.Select((t, i) => (t, i)).GroupBy(x => x.t.Name).ToDictionary(g => g.Key, g => g.First());
        string TypeName(IReadOnlyList<TypeTable.Entry> table, TypeTable.Info info) =>
            info.Type == TypeTable.NoType || info.Type >= table.Count ? "" : table[info.Type].Name;

        var changed = new Dictionary<string, bool>();
        bool Changed(string name)
        {
            if (changed.TryGetValue(name, out var c)) return c;
            changed[name] = false; // 循環の防ぎ (struct は循環しない)
            if (!oldByName.TryGetValue(name, out var o) || !newByName.TryGetValue(name, out var n)) return changed[name] = true;
            if (o.t.TKind != n.t.TKind || o.t.Fields.Length != n.t.Fields.Length) return changed[name] = true;
            for (var k = 0; k < n.t.Fields.Length; k++)
            {
                var (of, nf) = (o.t.Fields[k], n.t.Fields[k]);
                if (of.Name != nf.Name || of.Value.Kind != nf.Value.Kind
                    || TypeName(oldTypes, of.Value) != TypeName(newTypes, nf.Value))
                    return changed[name] = true;
                if (nf.Value.Kind == TypeTable.KindStruct && Changed(TypeName(newTypes, nf.Value))) return changed[name] = true;
            }
            return changed[name] = false;
        }

        var types = new List<TypePlan>();
        foreach (var (name, (n, _)) in newByName)
        {
            if (!oldByName.TryGetValue(name, out var o))
            {
                types.Add(new TypePlan(name, Added, []));
                continue;
            }
            if (o.t.TKind != n.TKind)
            {
                reasons.Add($"type {name} changed its kind");
                continue;
            }
            if (n.TKind is not (TypeTable.Class or TypeTable.Struct) || !Changed(name))
            {
                types.Add(new TypePlan(name, Same, []));
                continue;
            }
            var fields = n.Fields.Select(nf =>
            {
                // ADDED と RESET は initializer があればそれを、無ければ 0 の値 (Lua の reload chunk と同じ)
                var init = initializers is not null && initializers.TryGetValue((name, nf.Name), out var f) ? (uint)(f + 1) : 0u;
                var at = o.t.Fields.IndexOf(o.t.Fields.FirstOrDefault(x => x.Name == nf.Name)!);
                if (at < 0) return new FieldPlan(FieldAdded, 0, init);
                var of = o.t.Fields[at];
                var same = of.Value.Kind == nf.Value.Kind && TypeName(oldTypes, of.Value) == TypeName(newTypes, nf.Value);
                return same ? new FieldPlan(FieldRetained, (ushort)at, 0) : new FieldPlan(FieldReset, 0, init);
            }).ToImmutableArray();
            types.Add(new TypePlan(name, Relayout, fields));
        }
        foreach (var name in oldByName.Keys.Where(k => !newByName.ContainsKey(k)))
            types.Add(new TypePlan(name, Removed, []));

        var oldSet = oldStatics.ToHashSet();
        var newSet = newStatics.ToHashSet();
        var statics = newSet.Select(s => new StaticPlan(s.Owner, s.Field, oldSet.Contains(s) ? Same : Added))
            .Concat(oldSet.Where(s => !newSet.Contains(s)).Select(s => new StaticPlan(s.Owner, s.Field, Removed)))
            .ToImmutableArray();
        return new Plan([.. reasons], [.. types], statics);
    }

    /// <summary>名前を先に集める (string 表を計画より前に書くため)。</summary>
    public static IEnumerable<string> Names(Plan plan) =>
        plan.Reasons.Concat(plan.Types.Select(t => t.Name)).Concat(plan.Statics.SelectMany(s => new[] { s.Owner, s.Field }));

    public static void Write(BinaryWriter w, Plan plan, Func<string, int> str)
    {
        w.Write(plan.Reasons.Length);
        foreach (var r in plan.Reasons) w.Write(str(r));
        w.Write(plan.Types.Length);
        foreach (var t in plan.Types)
        {
            w.Write(str(t.Name));
            w.Write(t.Change);
            if (t.Change != Relayout) continue;
            w.Write((ushort)t.Fields.Length);
            foreach (var f in t.Fields)
            {
                w.Write(f.Source);
                w.Write(f.OldField);
                w.Write(f.Init);
            }
        }
        w.Write(plan.Statics.Length);
        foreach (var s in plan.Statics)
        {
            w.Write(str(s.Owner));
            w.Write(str(s.Field));
            w.Write(s.Change);
        }
    }
}
