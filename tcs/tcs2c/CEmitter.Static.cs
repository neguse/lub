using TinyCs;

namespace TinyCs.Tcs2c;

internal sealed partial class CEmitter
{
    private static string StaticInit(string name) => $"tcs_init_{Names.Id(name)}";

    private void EmitStaticInitPrototypes()
    {
        foreach (var cls in _program.Classes)
            Line($"static void {StaticInit(cls.Name)}(void);");
    }

    private void EmitStaticInitializer()
    {
        foreach (var cls in _program.Classes)
        {
            Line($"static void {StaticInit(cls.Name)}(void)");
            Line("{");
            _indent++;
            Line("static bool initialized;");
            Line("if (initialized) return;");
            Line("initialized = true;");
            _currentClass = cls;
            _scopes.Clear();
            PushScope();
            foreach (var field in cls.Fields.Where(f => f.IsStatic))
            {
                var fact = _facts.Field(cls.Name, field.Name);
                if (fact.Init is null) continue;
                RequireAssignable(fact.Type, TypeOf(fact.Init),
                    $"initializer of {cls.Name}.{field.Name}");
                Line($"{Names.StaticField(cls.Name, field.Name)} = {RenderExpr(fact.Init)};");
            }
            PopScope();
            _indent--;
            Line("}");
        }
        Line("static void tcs_init_statics(void)");
        Line("{");
        _indent++;
        foreach (var cls in _program.Classes) Line($"{StaticInit(cls.Name)}();");
        _indent--;
        Line("}");
        Line();
    }
}
