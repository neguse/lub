namespace TinyCs;

// reload chunk (HotReload) で移行できない編集を v1 / v2 の IL metadata の差分から
// 見つける (doc/hot-reload-design.md §11 の規則表、規則は ReloadRules)。戻り値は
// restart の理由で、空なら live に当てられる
public static class ReloadClassifier
{
    public static List<string> RestartReasons(IlExportResult v1, IlExportResult v2) =>
        ReloadRules.RestartReasons(Types(v1), Types(v2), migratesInstances: true);

    // --ref の class は host の table、interface は Lua 出力を持たない。
    // struct の static member は TCS1001
    private static IEnumerable<ReloadType> Types(IlExportResult export) =>
        export.Classes.Where(c => !c.IsExternal && !c.IsInterface)
            .Select(c => new ReloadType(c.Name, c.IsRecord ? "record" : "class",
                c.BaseName, "",
                [.. c.Fields.Where(f => f.IsStatic)
                    .Select(f => new ReloadStatic(f.Name, f.Type, f.InitHash, Pure: true))],
                []))
            .Concat((export.Structs.IsDefault ? [] : export.Structs)
                .Select(s => new ReloadType(s.Name, "struct", null, "", [], [])))
            .Concat((export.EnumTypes.IsDefault ? [] : export.EnumTypes)
                .Select(e => new ReloadType(e.Name, "enum", null, "", [], [.. e.Members])));
}
