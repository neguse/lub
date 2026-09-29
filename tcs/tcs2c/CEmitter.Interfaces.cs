namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    private void EmitInterfaceChecks()
    {
        foreach (var iface in _program.Classes.Where(c => c.IsInterface))
        {
            Line($"static bool tcs_is_{Names.Id(iface.Name)}(void *object)");
            Line("{");
            Line("    if (object == NULL) return false;");
            Line("    switch (((TcsObjectHeader *)object)->type_id) {");
            foreach (var cls in _program.Classes.Where(c => !c.IsInterface
                && IsAncestorOrSame(iface.Name, c.Name)))
                Line($"    case {Names.TypeId(cls.Name)}: return true;");
            Line("    default: return false;");
            Line("    }");
            Line("}");
        }
    }
}
