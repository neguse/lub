using System.Text;
using TinyCs;

namespace TinyCs.Tcs2c;

// struct の C 側 emit: typedef 生成。値型はポインタなしの素の
// C struct に落ち、copy は値代入で成立する。
internal sealed partial class CEmitter
{
    // struct typedef。struct-in-struct は値埋め込みに完全型が要るため
    // 内側が先 (struct は循環できないので必ず解ける)
    private void EmitStructTypedefs()
    {
        if (_program.Structs.IsDefault || _program.Structs.Length == 0)
            return;
        var pending = _program.Structs.ToList();
        var emitted = new HashSet<string>();
        while (pending.Count > 0)
        {
            var next = pending.FirstOrDefault(s => s.Fields.All(f =>
                _facts.MapType(f.Type) is not { Kind: CTypeKind.StructVal } embedded
                || emitted.Contains(embedded.Name!)))
                ?? throw new Tcs2cException("struct dependency cycle");
            pending.Remove(next);
            emitted.Add(next.Name);
            Line($"typedef struct {{");
            _indent++;
            foreach (var field in next.Fields)
                Line($"{_facts.MapType(field.Type).CName} " +
                    $"{Names.Field(field.Name)};");
            _indent--;
            Line($"}} Tcs_{Names.Id(next.Name)};");
            Line($"typedef struct {{ bool has; Tcs_{Names.Id(next.Name)} v; }} " +
                $"{CType.Nullable(CType.Struct(next.Name)).CName};");
            Line();
        }
    }

    // receiver を書き換える place (method の self / field への store) の lvalue と、
    // それを所有する heap object (stack 上の値なら null)。heap object の式は
    // setup の temp に 1 回だけ評価し、lvalue と owner の両方がその temp を指す。
    // struct method の本文中の self の owner は呼び出し側から渡る v_owner
    private (string Place, string? Owner) RenderOwnedStructPlace(IlExpr expr,
        StringBuilder setup)
    {
        switch (expr)
        {
            case IlVar v when v.Name == "self" && _structSelfOwner:
                return (RenderStructPlace(v), "v_owner");
            case IlVar v:
                return (RenderStructPlace(v),
                    Resolve(v.Name) is { Boxed: true } cell ? cell.CName : null);
            case IlField field when TryStaticField(field, out var staticName, out _):
                return (staticName, null);
            case IlField field when TypeOf(field.Recv).Kind == CTypeKind.StructVal:
            {
                var (inner, owner) = RenderOwnedStructPlace(field.Recv, setup);
                return ($"{inner}.{Names.Field(field.Name)}", owner);
            }
            case IlField field when TypeOf(field.Recv) is { Kind: CTypeKind.Ref } classType:
            {
                var owner = Temp("owner");
                setup.Append($"{classType.CName} {owner} = ({classType.CName})" +
                    $"tcs_nonnull({RenderExpr(field.Recv)}); ");
                return ($"{owner}->{Names.Field(field.Name)}", owner);
            }
            case IlIndex { PlusOne: true } index:
            {
                var sequenceType = RequireSequence(index);
                var owner = Temp("owner");
                setup.Append($"{sequenceType.CName} {owner} = {RenderExpr(index.Recv)}; ");
                var at = sequenceType.Kind == CTypeKind.Array
                    ? "tcs_array_at" : "tcs_list_at";
                return ($"(*({sequenceType.ElementCName} *){at}(" +
                    $"{owner}, {RenderExpr(index.Idx)}))", owner);
            }
            default:
                return (RenderStructPlace(expr), null);
        }
    }

    // struct 値の lvalue 連鎖。receiver/添字は inline 評価 (temp を挟むと
    // statement-expression になり lvalue 性が消える)
    private string RenderStructPlace(IlExpr expr)
    {
        switch (expr)
        {
            case IlVar v:
                var variable = Resolve(v.Name);
                return variable.Boxed ? $"(*{variable.CName})" : variable.CName;
            case IlIndex index when !index.PlusOne
                && TypeOf(index.Recv).Kind == CTypeKind.Dict:
            {
                // Dictionary の struct 値 (読みのみ — 書きは C# が CS1612 で拒否)
                var valueType = RequireDict(index.Recv, out var dictType);
                return $"(*({valueType.CName} *)tcs_dict_at(" +
                    $"{RenderExpr(index.Recv)}, {DictKeyArgs(dictType.Key!, index.Idx)}))";
            }
            case IlIndex index:
            {
                var sequenceType = RequireSequence(index);
                var at = sequenceType.Kind == CTypeKind.Array
                    ? "tcs_array_at" : "tcs_list_at";
                return $"(*({sequenceType.ElementCName} *){at}(" +
                    $"{RenderExpr(index.Recv)}, {RenderExpr(index.Idx)}))";
            }
            case IlField field:
            {
                if (TryStaticField(field, out var staticName, out _))
                    return staticName;
                var receiverType = TypeOf(field.Recv);
                if (receiverType.Kind == CTypeKind.StructVal)
                    return $"{RenderStructPlace(field.Recv)}." +
                        Names.Field(field.Name);
                if (receiverType.Kind == CTypeKind.Ref)
                    return $"(({receiverType.CName})tcs_nonnull(" +
                        $"{RenderExpr(field.Recv)}))->{Names.Field(field.Name)}";
                if (receiverType.Kind == CTypeKind.Kvp)
                    // KeyValuePair の struct 値 (読みのみ — kv.Value.F への書きは
                    // C# が CS1612 で拒否、mutating method は IsStructPlace が copy に回す)
                    return RenderField(field);
                throw new Tcs2cException(
                    "unsupported struct place receiver: " + receiverType);
            }
            case IlParen paren:
                return $"({RenderStructPlace(paren.E)})";
            case IlNullableValue or IlIife or IlCall or IlInvoke or IlDynCall
                or IlWith or IlStructCopy or IlNewObj or IlTernary:
                // rvalue の struct (読みのみ。C は rvalue struct の member を読める)
                return $"({RenderExpr(expr)})";
            default:
                throw new Tcs2cException(
                    $"unsupported struct place: {expr.GetType().Name}");
        }
    }
}
