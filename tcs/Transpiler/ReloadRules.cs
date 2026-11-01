using System.Globalization;
using System.Text;

namespace TinyCs;

// hot reload で live に当てられない編集の規則 (doc/hot-reload-design.md §11 の
// 規則表)。reload chunk (ReloadClassifier、IlExport の metadata) と module registry
// (IncrementalCompilationSession、emitter の記録) が、それぞれの metadata をこの形に
// 写して同じ規則で判定する。
public sealed record ReloadStatic(string Name, string Type, string? InitHash, bool Pure);

public sealed record ReloadType(
    string Name,
    string Kind, // class | record | struct | enum
    string? BaseName,
    string InstanceShape,
    IReadOnlyList<ReloadStatic> Statics,
    IReadOnlyList<(string Name, int Value)> EnumMembers);

public static class ReloadRules
{
    // 戻り値は restart の理由で、空なら live。migratesInstances は reload chunk:
    // instance field を registry 経由で移行し、新しい static の initializer を chunk の
    // 中で評価するので、instance shape の変更と副作用のある static の追加を restart に
    // しない。module registry (false) は既存 instance を移行せず、transaction の中で
    // 副作用のある initializer を実行しないので、どちらも restart にする
    public static List<string> RestartReasons(IEnumerable<ReloadType> v1,
        IEnumerable<ReloadType> v2, bool migratesInstances)
    {
        var reasons = new List<string>();
        var next = v2.ToDictionary(t => t.Name);
        foreach (var old in v1)
        {
            if (!next.TryGetValue(old.Name, out var t))
            {
                reasons.Add($"type removed: {old.Name}");
                continue;
            }
            if (old.Kind != t.Kind)
            {
                reasons.Add($"type kind changed: {t.Name}");
                continue;
            }
            if (old.BaseName != t.BaseName)
                reasons.Add($"base changed: {t.Name}");
            if (!migratesInstances && old.InstanceShape != t.InstanceShape)
                reasons.Add($"instance shape changed: {t.Name}");
            StaticReasons(old, t, migratesInstances, reasons);
            EnumReasons(old, t, reasons);
        }
        return reasons;
    }

    // 既存 static は生存値を保持するので、initializer の変更は反映されない。型の変更
    // も restart: 保持した値は旧い型のまま (Lua の int と float は別の値)。const も
    // static と同じ: 使う側には埋め込まれるが、それを使う static の initializer は
    // 再評価されない
    private static void StaticReasons(ReloadType old, ReloadType t,
        bool migratesInstances, List<string> reasons)
    {
        var oldStatics = old.Statics.ToDictionary(s => s.Name);
        foreach (var s in t.Statics)
        {
            if (oldStatics.TryGetValue(s.Name, out var os))
            {
                if (os.Type != s.Type)
                    reasons.Add($"static type changed: {t.Name}.{s.Name}");
                else if (os.InitHash != s.InitHash)
                    reasons.Add($"static initializer changed: {t.Name}.{s.Name}");
            }
            else if (!migratesInstances && !s.Pure)
            {
                reasons.Add($"impure new static: {t.Name}.{s.Name}");
            }
        }
    }

    // 保存済みの enum 値 (int) は読み替えられない。member の追加だけが live
    private static void EnumReasons(ReloadType old, ReloadType t, List<string> reasons)
    {
        var members = t.EnumMembers.ToDictionary(m => m.Name, m => m.Value);
        foreach (var (member, value) in old.EnumMembers)
        {
            if (!members.TryGetValue(member, out var newValue) || newValue != value)
                reasons.Add($"enum member removed or renumbered: {t.Name}.{member}");
        }
    }
}
