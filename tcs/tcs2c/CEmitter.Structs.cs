using TinyCs;

namespace TinyCs.Tcs2c;

// struct (T219b) の C 側 emit: typedef 生成。値型はポインタなしの素の
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
                !_facts.Structs.ContainsKey(f.Type)
                || emitted.Contains(f.Type)))
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
            Line();
        }
    }
}
