using System.Text;
using TinyCs;

namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    private string RenderArrayLiteral(IlTable table)
    {
        var type = TypeOfTable(table);
        var name = Temp("array");
        var text = new StringBuilder($"TcsArray *{name} = tcs_array_new(" +
            $"{RuntimeTypeId(type)}, {table.Entries.Length}, sizeof({type.ElementCName}), {TraceValue(type.Element)}); ");
        for (var i = 0; i < table.Entries.Length; i++)
            text.Append($"(({type.ElementCName} *){name}->data)[{i}] = " +
                $"{RenderCoerced(table.Entries[i].Value, type.Element!)}; ");
        return $"({{ {text}{name}; }})";
    }
}
