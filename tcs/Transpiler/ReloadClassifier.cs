namespace TinyCs;

// reload chunk (HotReload) で移行できない編集を v1 / v2 の IL metadata の差分から
// 見つける (doc/hot-reload-design.md §5.2 の restart)。戻り値は restart の理由で、
// 空なら live に当てられる。instance field の追加・削除・型変更は registry 経由で
// 移行するので restart にしない (module registry の IncrementalCompilationSession
// とはここが違う)
public static class ReloadClassifier
{
    public static List<string> RestartReasons(IlExportResult v1, IlExportResult v2)
    {
        var reasons = new List<string>();
        var oldKinds = TypeKinds(v1);
        var newKinds = TypeKinds(v2);
        foreach (var (name, kind) in oldKinds)
        {
            if (!newKinds.TryGetValue(name, out var newKind))
                reasons.Add($"type removed: {name}");
            else if (newKind != kind)
                reasons.Add($"type kind changed: {name}");
        }

        var oldClasses = Classes(v1).ToDictionary(c => c.Name);
        foreach (var cls in Classes(v2))
        {
            if (!oldClasses.TryGetValue(cls.Name, out var old))
                continue;
            if (old.BaseName != cls.BaseName)
                reasons.Add($"base changed: {cls.Name}");
            // 既存 static は生存値を保持するので、initializer の変更は反映されない。
            // const も restart: 使う側には埋め込まれるが、それを使う static の
            // initializer は再評価されない
            var oldStatics = old.Fields.Where(f => f.IsStatic)
                .ToDictionary(f => f.Name);
            foreach (var field in cls.Fields.Where(f => f.IsStatic))
            {
                if (oldStatics.TryGetValue(field.Name, out var oldField)
                    && oldField.Type == field.Type
                    && oldField.InitHash != field.InitHash)
                    reasons.Add($"static initializer changed: {cls.Name}.{field.Name}");
            }
        }

        // 保存済みの enum 値 (int) は読み替えられない。member の追加だけが live
        var newEnums = Enums(v2).ToDictionary(e => e.Name);
        foreach (var old in Enums(v1))
        {
            if (!newEnums.TryGetValue(old.Name, out var @new))
                continue;
            var newMembers = @new.Members.ToDictionary(m => m.Name, m => m.Value);
            foreach (var (member, value) in old.Members)
            {
                if (!newMembers.TryGetValue(member, out var newValue) || newValue != value)
                    reasons.Add($"enum member removed or renumbered: {old.Name}.{member}");
            }
        }
        return reasons;
    }

    // --ref の class は host の table、interface は Lua 出力を持たない
    private static IEnumerable<IlClassInfo> Classes(IlExportResult export) =>
        export.Classes.Where(c => !c.IsExternal && !c.IsInterface);

    private static IEnumerable<IlEnumInfo> Enums(IlExportResult export) =>
        export.EnumTypes.IsDefault ? [] : export.EnumTypes;

    private static Dictionary<string, string> TypeKinds(IlExportResult export) =>
        Classes(export).Select(c => (c.Name, Kind: c.IsRecord ? "record" : "class"))
            .Concat((export.Structs.IsDefault ? [] : export.Structs)
                .Select(s => (s.Name, Kind: "struct")))
            .Concat(Enums(export).Select(e => (e.Name, Kind: "enum")))
            .ToDictionary(t => t.Name, t => t.Kind);
}
