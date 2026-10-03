using System.Text;
using TinyCs;

namespace TinyCs.Tcs2c;

// 生成式: class / struct の new、List / Dictionary リテラル (IlTable)、
// 固定長配列 (IlNewArray)。
internal sealed partial class CEmitter
{
    private string RenderNew(IlNewObj creation)
    {
        if (IsRandomTypeName(creation.TypeName)) return RenderRandomNew(creation);
        if (_facts.Structs.ContainsKey(creation.TypeName))
        {
            // struct の zero 値。明示 ctor 呼び (S.ctor) は IlCall 経由なので
            // ここに args 付きでは来ない
            if (creation.Args.Length != 0)
                throw new Tcs2cException(
                    $"struct constructor is not supported by the C backend: " +
                    creation.TypeName);
            return $"(({CType.Struct(creation.TypeName).CName}){{0}})";
        }
        if (!_classes.TryGetValue(creation.TypeName, out var cls))
            throw new Tcs2cException($"unknown class: {creation.TypeName}");
        if (cls.IsInterface)
            throw new Tcs2cException($"cannot instantiate interface: {cls.Name}");
        var paramFacts = CtorParamFacts(cls);
        var args = CompleteArguments(paramFacts, creation.Args, $"constructor {cls.Name}");
        if (args.Count == 0) return $"{Names.New(cls.Name)}()";
        var values = new List<(CType Type, string Value)>();
        for (var i = 0; i < args.Count; i++)
        {
            CheckAssignable(paramFacts[i].Type, args[i],
                $"constructor argument {i} of {cls.Name}");
            values.Add((paramFacts[i].Type,
                RenderCoerced(args[i], paramFacts[i].Type)));
        }
        return RenderOrderedCall(Names.New(cls.Name),
            CType.Ref(cls.Name), values);
    }

    private CType TypeOfTable(IlTable table)
    {
        if (table.ObjectType is not null) return _facts.MapType(table.ObjectType);
        if (table.KeyType is not null
            || table.Entries.Any(e => e.Key is not null))
            return TypeOfDictTable(table);
        if (table.Entries.Any(e => e.NameKey is not null))
            throw new Tcs2cException("option-table IlTable is not supported");
        CType? element = table.ElementType is null
            ? null : _facts.MapType(table.ElementType);
        foreach (var entry in table.Entries)
        {
            // 契約の要素型があれば各項はそれへ代入可能 (派生 → 基底の upcast
            // を含む) であれば良い。無ければ項の共通型で推論する
            if (element is not null && table.ElementType is not null)
            {
                CheckAssignable(element, entry.Value, "IlTable item");
                continue;
            }
            var itemType = TypeOf(entry.Value);
            element = element is null ? itemType : CommonType(element, itemType, "IlTable items");
        }
        if (element is not null && !IsStorageType(element))
            throw new Tcs2cException($"unsupported List element type: {element}");
        if (table.IsArray)
            return CType.Array(element
                ?? throw new Tcs2cException("array literal needs an element type"));
        return CType.List(element);
    }

    // 固定長配列 literal (new T[] { ... }): 要素数で確保して順に store
    private string RenderArrayLiteral(IlTable table, CType type)
    {
        var array = Temp("array");
        var statements = new StringBuilder(
            $"TcsArray *{array} = tcs_typed(tcs_array_new({table.Entries.Length}, " +
            $"sizeof({type.ElementCName}), {LayoutRef(type.Element!)}), {RuntimeTypeId(type)}); ");
        for (var i = 0; i < table.Entries.Length; i++)
            statements.Append($"(({type.ElementCName} *){array}->data)[{i}] = " +
                $"{RenderCoerced(table.Entries[i].Value, type.Element!)}; ");
        return $"({{ {statements}{array}; }})";
    }

    private string RenderTable(IlTable table)
    {
        if (table.ObjectType is not null)
            return RenderObjectInitializer(table, TypeOfTable(table));
        if (table.KeyType is not null || table.Entries.Any(e => e.Key is not null))
            return RenderDictTable(table);
        var type = TypeOfTable(table);
        if (type.Kind == CTypeKind.Array) return RenderArrayLiteral(table, type);
        var list = Temp("list");
        var elementSize = type.Element is null ? "0" : $"sizeof({type.ElementCName})";
        var layout = type.Element is null ? "NULL" : LayoutRef(type.Element);
        var statements = new StringBuilder(
            $"TcsList *{list} = tcs_typed(tcs_list_new({elementSize}, {layout}), " +
            $"{RuntimeTypeId(type)}); ");
        foreach (var entry in table.Entries)
        {
            var value = Temp("list_item");
            statements.Append(type.ElementCName).Append(' ').Append(value)
                .Append(" = ").Append(RenderCoerced(entry.Value, type.Element!))
                .Append("; ")
                .Append("tcs_list_add(").Append(list).Append(", &").Append(value)
                .Append(", sizeof(").Append(value).Append("), ")
                .Append(layout).Append("); ");
        }
        return $"({{ {statements}{list}; }})";
    }

    private CType TypeOfNewArray(IlNewArray array)
    {
        RequireType(CType.I32, TypeOf(array.Length), "array length");
        var element = _facts.MapType(array.ElementType);
        var result = CType.Array(element);
        EnsureSupportedStorageType(result, "IlNewArray element type");
        return result;
    }

    private string RenderNewArray(IlNewArray array)
    {
        var type = TypeOfNewArray(array);
        return $"tcs_typed(tcs_array_new({RenderExpr(array.Length)}, " +
            $"sizeof({type.ElementCName}), {LayoutRef(type.Element!)}), {RuntimeTypeId(type)})";
    }
}
