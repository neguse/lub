namespace TinyCs;

// v1 / v2 の IL の metadata (IlExportResult) の差分から、live に当てられない編集を見つける (doc/hot-reload-design.md §11)。
// 規則の本体は SelfHost/Tool/Reload.cs の ReloadRules (TinyC#、reload chunk と増分 session と同じ)。
// 戻り値は restart の理由で、空なら live に当てられる。tcs VM の reload (tcsvm/Emitter) が使う
public static class ReloadClassifier
{
    public static List<string> RestartReasons(IlExportResult v1, IlExportResult v2) =>
        TcsSelf.ReloadRules.RestartReasons(Types(v1), Types(v2), true);

    // --ref の class は host の table、interface は出力を持たない
    private static List<TcsSelf.ReloadType> Types(IlExportResult export)
    {
        var list = new List<TcsSelf.ReloadType>();
        foreach (var c in export.Classes.Where(c => !c.IsExternal && !c.IsInterface))
        {
            var t = new TcsSelf.ReloadType(c.Name, c.IsRecord ? "record" : "class") { BaseName = c.BaseName ?? "" };
            t.Statics.AddRange(c.Fields.Where(f => f.IsStatic).Select(Static));
            list.Add(t);
        }
        foreach (var s in export.Structs.IsDefault ? [] : export.Structs)
        {
            var t = new TcsSelf.ReloadType(s.Name, "struct");
            t.Statics.AddRange((s.StaticFields.IsDefault ? [] : s.StaticFields).Select(Static));
            list.Add(t);
        }
        foreach (var e in export.EnumTypes.IsDefault ? [] : export.EnumTypes)
        {
            var t = new TcsSelf.ReloadType(e.Name, "enum");
            foreach (var (name, value) in e.Members)
            {
                t.EnumMemberNames.Add(name);
                t.EnumMemberValues.Add(value);
            }
            list.Add(t);
        }
        return list;
    }

    private static TcsSelf.ReloadStatic Static(IlFieldInfo f) => new(f.Name, f.Type, f.InitHash, true);
}
