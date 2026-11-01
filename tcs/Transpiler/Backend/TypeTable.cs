using System.Collections.Immutable;

namespace TinyCs.Backend;

/// <summary>VM と tcs2c が共有する型記述子 (doc/object-model.md §4)。backend は自分の型を
/// この形に写し、<see cref="TypeTable.Write"/> が byte 列 (v1) にする。C の runtime/model が読む。</summary>
public static class TypeTable
{
    public const byte KindI32 = 0, KindF32 = 1, KindBool = 2, KindRef = 3, KindStruct = 4;
    public const byte Class = 1, Struct = 2, Array = 3, String = 4, List = 5, Dict = 6, Func = 7, Iface = 8, Object = 9, Box = 10;
    public const byte FlagRecord = 1, FlagHasOnReload = 2;
    public const ushort NoType = 0xFFFF;

    /// <summary>値の種類と型の番号 (REF の参照先、STRUCT の struct)。</summary>
    public readonly record struct Info(byte Kind, ushort Type);

    public sealed record Field(string Name, Info Value);

    public sealed record Entry(byte TKind, string Name)
    {
        public uint LayoutHash { get; init; }
        public byte Flags { get; init; }
        public ImmutableArray<Field> Fields { get; init; } = [];
        public ImmutableArray<ushort> Interfaces { get; init; } = [];
        public ImmutableArray<(ushort Selector, ushort Func)> Itable { get; init; } = [];
        public Info Element { get; init; }
        public Info Key { get; init; }
        public ImmutableArray<Info> Params { get; init; } = [];
        public ImmutableArray<Info> Rets { get; init; } = [];
    }

    /// <summary>IlExport の LayoutHash (8 桁の 16 進) を数に。</summary>
    public static uint ParseLayoutHash(string? hex) =>
        hex is { Length: > 0 } ? uint.Parse(hex, System.Globalization.NumberStyles.HexNumber) : 0;

    /// <summary>型の表を書く。名前は str で string 表の番号にする (表は呼ぶ側が前に書く)。</summary>
    public static void Write(BinaryWriter w, IReadOnlyList<Entry> types, Func<string, int> str)
    {
        w.Write(types.Count);
        foreach (var t in types)
        {
            w.Write(t.TKind);
            w.Write(str(t.Name));
            w.Write(t.LayoutHash);
            w.Write(t.Flags);
            switch (t.TKind)
            {
                case Class or Struct:
                    w.Write((ushort)t.Fields.Length);
                    foreach (var f in t.Fields)
                    {
                        w.Write(str(f.Name));
                        w.Write(f.Value.Kind);
                        w.Write(f.Value.Type);
                    }
                    if (t.TKind == Class)
                    {
                        w.Write((ushort)t.Interfaces.Length);
                        foreach (var i in t.Interfaces) w.Write(i);
                        w.Write((ushort)t.Itable.Length);
                        foreach (var (sel, fn) in t.Itable)
                        {
                            w.Write(sel);
                            w.Write(fn);
                        }
                    }
                    break;
                case Array or List:
                    WriteInfo(w, t.Element);
                    break;
                case Dict:
                    WriteInfo(w, t.Key);
                    WriteInfo(w, t.Element);
                    break;
                case Func:
                    w.Write((ushort)t.Params.Length);
                    foreach (var p in t.Params) WriteInfo(w, p);
                    w.Write((ushort)t.Rets.Length);
                    foreach (var r in t.Rets) WriteInfo(w, r);
                    break;
                case Box:
                    w.Write(t.Element.Kind);
                    break;
            }
        }
    }

    /// <summary>名前を先に集める (string 表を型の表より前に書くため)。</summary>
    public static IEnumerable<string> Names(IReadOnlyList<Entry> types) =>
        types.Select(t => t.Name).Concat(types.SelectMany(t => t.Fields.Select(f => f.Name)));

    private static void WriteInfo(BinaryWriter w, Info info)
    {
        w.Write(info.Kind);
        w.Write(info.Type);
    }
}
