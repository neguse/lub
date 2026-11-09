using System.Collections.Generic;
using TcsC;

namespace TcsVm;

public sealed class FieldPlan
{
    public int Source;
    public int OldField;
    public int Init;

    public FieldPlan(int source, int oldField, int init)
    {
        Source = source;
        OldField = oldField;
        Init = init;
    }
}

public sealed class TypePlan
{
    public string Name;
    public int Change;
    public List<FieldPlan> Fields;

    public TypePlan(string name, int change, List<FieldPlan> fields)
    {
        Name = name;
        Change = change;
        Fields = fields;
    }
}

public sealed class StaticPlan
{
    public string Owner;
    public string Field;
    public int Change;

    public StaticPlan(string owner, string field, int change)
    {
        Owner = owner;
        Field = field;
        Change = change;
    }
}

public sealed class Plan
{
    public List<string> Reasons;
    public List<TypePlan> Types;
    public List<StaticPlan> Statics;

    public Plan(List<string> reasons, List<TypePlan> types, List<StaticPlan> statics)
    {
        Reasons = reasons;
        Types = types;
        Statics = statics;
    }
}

/// <summary>reload の計画 (doc/object-model.md §6)。旧版と新版の型記述子 (TcsC.TypeTable) から、
/// 型ごとの retained / added / reset、static の差分、restart の理由を求め、byte 列 (v1) にする。
/// VM の tvm_apply と tcs2c の migrator が同じ構造で受ける (C の runtime/model/tcs_walk が読む)。
/// 型の並びは名前が最初に現れた順 (C# 版の GroupBy と Dictionary の挿入順)。</summary>
public static class ReloadPlan
{
    public const int Same = 0;
    public const int Added = 1;
    public const int Removed = 2;
    public const int Relayout = 3;
    public const int FieldRetained = 0;
    public const int FieldAdded = 1;
    public const int FieldReset = 2;

    // 名前が最初に現れた位置 (同じ名前の 2 つ目からは無視する)
    private static List<string> FirstNames(List<TypeTableEntry> types, Dictionary<string, int> at)
    {
        var order = new List<string>();
        for (int i = 0; i < types.Count; i++)
        {
            if (at.ContainsKey(types[i].Name)) continue;
            at[types[i].Name] = i;
            order.Add(types[i].Name);
        }
        return order;
    }

    private static string TypeName(List<TypeTableEntry> table, TypeTableInfo info) =>
        info.Type == TypeTable.NoType || info.Type >= table.Count ? "" : table[info.Type].Name;

    private static bool Changed(string name, List<TypeTableEntry> oldTypes, List<TypeTableEntry> newTypes,
        Dictionary<string, int> oldAt, Dictionary<string, int> newAt, Dictionary<string, bool> changed)
    {
        bool c;
        if (changed.TryGetValue(name, out c)) return c;
        changed[name] = false; // 循環の防ぎ (struct は循環しない)
        int oi;
        int ni;
        if (!oldAt.TryGetValue(name, out oi) || !newAt.TryGetValue(name, out ni))
        {
            changed[name] = true;
            return true;
        }
        var o = oldTypes[oi];
        var n = newTypes[ni];
        if (o.TKind != n.TKind || o.Fields.Count != n.Fields.Count)
        {
            changed[name] = true;
            return true;
        }
        for (int k = 0; k < n.Fields.Count; k++)
        {
            var of = o.Fields[k];
            var nf = n.Fields[k];
            if (of.Name != nf.Name || of.Value.Kind != nf.Value.Kind
                || TypeName(oldTypes, of.Value) != TypeName(newTypes, nf.Value))
            {
                changed[name] = true;
                return true;
            }
            if (nf.Value.Kind == TypeTable.KindStruct && Changed(TypeName(newTypes, nf.Value), oldTypes, newTypes, oldAt, newAt, changed))
            {
                changed[name] = true;
                return true;
            }
        }
        changed[name] = false;
        return false;
    }

    /// <summary>型を名前で対応させる。field は名前で対応させ、種類と型 (の名前) が同じなら retained、違えば reset
    /// (hot-reload §5.2: 新しい型の 0 の値)、新しい名前は added。struct の field は、その struct の形が変わった
    /// とき owner も relayout になる (layout hash が展開して伝わるのと同じ)。
    /// initializers は "型\nfield" → 関数の番号。</summary>
    public static Plan Compute(List<TypeTableEntry> oldTypes, List<TypeTableEntry> newTypes,
        List<StaticKey> oldStatics, List<StaticKey> newStatics, List<string> restartReasons,
        Dictionary<string, int>? initializers)
    {
        var reasons = new List<string>();
        foreach (var r in restartReasons) reasons.Add(r);
        var oldAt = new Dictionary<string, int>();
        var newAt = new Dictionary<string, int>();
        var oldOrder = FirstNames(oldTypes, oldAt);
        var newOrder = FirstNames(newTypes, newAt);
        var changed = new Dictionary<string, bool>();

        var types = new List<TypePlan>();
        foreach (var name in newOrder)
        {
            var n = newTypes[newAt[name]];
            int oi;
            if (!oldAt.TryGetValue(name, out oi))
            {
                types.Add(new TypePlan(name, Added, new List<FieldPlan>()));
                continue;
            }
            var o = oldTypes[oi];
            if (o.TKind != n.TKind)
            {
                reasons.Add("type " + name + " changed its kind");
                continue;
            }
            if (!(n.TKind == TypeTable.Class || n.TKind == TypeTable.Struct) || !Changed(name, oldTypes, newTypes, oldAt, newAt, changed))
            {
                types.Add(new TypePlan(name, Same, new List<FieldPlan>()));
                continue;
            }
            var fields = new List<FieldPlan>();
            foreach (var nf in n.Fields)
            {
                // ADDED と RESET は initializer があればそれを、無ければ 0 の値
                int f;
                int init = initializers != null && initializers.TryGetValue(name + "\n" + nf.Name, out f) ? f + 1 : 0;
                int at = -1;
                for (int k = 0; k < o.Fields.Count; k++)
                {
                    if (o.Fields[k].Name == nf.Name)
                    {
                        at = k;
                        break;
                    }
                }
                if (at < 0)
                {
                    fields.Add(new FieldPlan(FieldAdded, 0, init));
                    continue;
                }
                var of = o.Fields[at];
                bool same = of.Value.Kind == nf.Value.Kind && TypeName(oldTypes, of.Value) == TypeName(newTypes, nf.Value);
                fields.Add(same ? new FieldPlan(FieldRetained, at, 0) : new FieldPlan(FieldReset, 0, init));
            }
            types.Add(new TypePlan(name, Relayout, fields));
        }
        foreach (var name in oldOrder)
        {
            if (!newAt.ContainsKey(name)) types.Add(new TypePlan(name, Removed, new List<FieldPlan>()));
        }

        // static は (型、field) の集合 (足した順。同じ組は 1 つ)
        var oldKeys = new HashSet<string>();
        var oldItems = new List<StaticKey>();
        foreach (var s in oldStatics)
        {
            string k = s.Owner + "\n" + s.Field;
            if (oldKeys.Contains(k)) continue;
            oldKeys.Add(k);
            oldItems.Add(s);
        }
        var newKeys = new HashSet<string>();
        var newItems = new List<StaticKey>();
        foreach (var s in newStatics)
        {
            string k = s.Owner + "\n" + s.Field;
            if (newKeys.Contains(k)) continue;
            newKeys.Add(k);
            newItems.Add(s);
        }
        var statics = new List<StaticPlan>();
        foreach (var s in newItems)
            statics.Add(new StaticPlan(s.Owner, s.Field, oldKeys.Contains(s.Owner + "\n" + s.Field) ? Same : Added));
        foreach (var s in oldItems)
        {
            if (!newKeys.Contains(s.Owner + "\n" + s.Field)) statics.Add(new StaticPlan(s.Owner, s.Field, Removed));
        }
        return new Plan(reasons, types, statics);
    }

    /// <summary>名前を先に集める (string 表を計画より前に書くため)。</summary>
    public static List<string> Names(Plan plan)
    {
        var r = new List<string>();
        foreach (var x in plan.Reasons) r.Add(x);
        foreach (var t in plan.Types) r.Add(t.Name);
        foreach (var s in plan.Statics)
        {
            r.Add(s.Owner);
            r.Add(s.Field);
        }
        return r;
    }

    public static void Write(List<int> w, Plan plan, TypeTableStrings str)
    {
        VBytes.I32(w, plan.Reasons.Count);
        foreach (var r in plan.Reasons) VBytes.I32(w, str.Str(r));
        VBytes.I32(w, plan.Types.Count);
        foreach (var t in plan.Types)
        {
            VBytes.I32(w, str.Str(t.Name));
            VBytes.U8(w, t.Change);
            if (t.Change != Relayout) continue;
            VBytes.U16(w, t.Fields.Count);
            foreach (var f in t.Fields)
            {
                VBytes.U8(w, f.Source);
                VBytes.U16(w, f.OldField);
                VBytes.I32(w, f.Init);
            }
        }
        VBytes.I32(w, plan.Statics.Count);
        foreach (var s in plan.Statics)
        {
            VBytes.I32(w, str.Str(s.Owner));
            VBytes.I32(w, str.Str(s.Field));
            VBytes.U8(w, s.Change);
        }
    }
}
