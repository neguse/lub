using TinyCs;

namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    private readonly Dictionary<string, uint> _runtimeTypeIds = new();

    private static string TypeKey(CType type) => $"{type.Kind}:{type.Name}" +
        (type.Element is null ? "" : $"<{TypeKey(type.Element)}>") +
        (type.Key is null ? "" : $"[{TypeKey(type.Key)}]") +
        (type.Parameters is null ? "" : $"({string.Join(",", type.Parameters.Select(TypeKey))})");

    private string RuntimeTypeId(CType type)
    {
        if (type == CType.String) return "TCS_STRING";
        if (type == CType.I32) return "TCS_BOX_I32";
        if (type == CType.F32) return "TCS_BOX_F32";
        if (type == CType.Bool) return "TCS_BOX_BOOL";
        if (type.Kind == CTypeKind.Ref) return Names.TypeId(type.Name!);
        var key = TypeKey(type);
        if (!_runtimeTypeIds.TryGetValue(key, out var id))
            _runtimeTypeIds[key] = id = 0x80000000U + (uint)_runtimeTypeIds.Count;
        return $"UINT32_C({id})";
    }

    private string RenderBox(IlExpr value)
    {
        var type = TypeOf(value);
        if (type.IsNullable || type == CType.Null) return $"((void *){RenderExpr(value)})";
        var function = type.Kind switch
        {
            CTypeKind.I32 => "tcs_box_i32",
            CTypeKind.F32 => "tcs_box_f32",
            CTypeKind.Bool => "tcs_box_bool",
            _ => throw new Tcs2cException($"cannot box {type}"),
        };
        return $"{function}({RenderExpr(value)})";
    }

    private string RenderUnbox(IlExpr value, CType target)
    {
        var field = target.Kind switch
        {
            CTypeKind.I32 => "i", CTypeKind.F32 => "f", CTypeKind.Bool => "b",
            _ => throw new Tcs2cException($"cannot unbox {target}"),
        };
        return $"(tcs_unbox({RenderExpr(value)}, {RuntimeTypeId(target)})->value.{field})";
    }

    private string RenderRefCast(IlRefCast cast)
    {
        var target = _facts.MapType(cast.TargetType);
        if (target == CType.Object) return RenderBox(cast.Value);
        if (target == CType.Bool)
            return TypeOf(cast.Value) == CType.Object
                ? RenderUnbox(cast.Value, target) : RenderExpr(cast.Value);
        if (!target.IsNullable) throw new Tcs2cException($"unsupported cast to {target}");
        if (target.Kind == CTypeKind.Ref && _classes[target.Name!].IsInterface)
            return $"(({target.CName})tcs_interface_cast({RenderExpr(cast.Value)}, tcs_is_{Names.Id(target.Name!)}))";
        var first = RuntimeTypeId(target);
        var last = target.Kind == CTypeKind.Ref ? Names.TypeIdMax(target.Name!) : first;
        return $"(({target.CName})tcs_ref_cast({RenderExpr(cast.Value)}, {first}, {last}))";
    }

    private const string ObjectRuntime = """
        typedef struct TcsBox {
            uint32_t type_id;
            union { int32_t i; float f; bool b; } value;
        } TcsBox;

        static TcsBox *tcs_box_i32(int32_t value)
        {
            TcsBox *box = tcs_alloc(sizeof(*box));
            box->type_id = TCS_BOX_I32; box->value.i = value;
            return box;
        }
        static TcsBox *tcs_box_f32(float value)
        {
            TcsBox *box = tcs_alloc(sizeof(*box));
            box->type_id = TCS_BOX_F32; box->value.f = value;
            return box;
        }
        static TcsBox *tcs_box_bool(bool value)
        {
            TcsBox *box = tcs_alloc(sizeof(*box));
            box->type_id = TCS_BOX_BOOL; box->value.b = value;
            return box;
        }
        static TcsBox *tcs_unbox(void *value, uint32_t type_id)
        {
            tcs_nonnull(value);
            if (((TcsObjectHeader *)value)->type_id != type_id) tcs_fault("invalid-cast");
            return value;
        }
        static void *tcs_ref_cast(void *value, uint32_t first, uint32_t last)
        {
            if (value != NULL && !tcs_type_in_range(((TcsObjectHeader *)value)->type_id, first, last))
                tcs_fault("invalid-cast");
            return value;
        }
        static void *tcs_interface_cast(void *value, bool (*matches)(void *))
        {
            if (value != NULL && !matches(value)) tcs_fault("invalid-cast");
            return value;
        }

        """;
}
