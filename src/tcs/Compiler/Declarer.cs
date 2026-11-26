using System.Collections.Generic;
using TinySystem.Text;

namespace Tcs;

// Context for resolving types (position within a declaration)
public sealed class TypeScope
{
    public TypeDeclSym? Type;
    public string Namespace;
    public List<string> Usings;
    // Namespace in which each Usings element was written ("" is the compilation unit). Used to search extension methods from the inner scope outward
    public List<string>? UsingLevels;
    public List<string>? MethodTypeParams;
    // where T : struct of method type arguments (same order as MethodTypeParams)
    public List<bool>? MethodTypeParamStruct;
    public SourceFile File;
    // using X = A.B;
    public Dictionary<string, string>? Aliases;

    public TypeScope(SourceFile file, string ns, List<string> usings)
    {
        File = file;
        Namespace = ns;
        Usings = usings;
    }

    public TypeScope WithType(TypeDeclSym t)
    {
        var s = new TypeScope(File, Namespace, Usings);
        s.UsingLevels = UsingLevels;
        s.Type = t;
        s.MethodTypeParams = MethodTypeParams;
        s.MethodTypeParamStruct = MethodTypeParamStruct;
        s.Aliases = Aliases;
        return s;
    }

    public TypeScope WithMethodTypeParams(List<string>? tps)
    {
        var s = new TypeScope(File, Namespace, Usings);
        s.UsingLevels = UsingLevels;
        s.Type = Type;
        s.MethodTypeParams = tps;
        s.Aliases = Aliases;
        return s;
    }

    public TypeScope WithMethodTypeParams(List<string>? tps, List<bool>? structs)
    {
        var s = WithMethodTypeParams(tps);
        s.MethodTypeParamStruct = structs;
        return s;
    }
}

// Declarations of the whole program
public sealed class Compilation
{
    public List<SourceFile> Files = new List<SourceFile>();
    public List<Node> Units = new List<Node>();
    public Diagnostics Diags = new Diagnostics();
    public List<TypeDeclSym> Types = new List<TypeDeclSym>();
    public Dictionary<string, TypeDeclSym> TypesByFullName = new Dictionary<string, TypeDeclSym>();
    public HashSet<string> Namespaces = new HashSet<string>();
    // Type resolution context per type declaration
    // Extension methods of static classes (by name)
    public Dictionary<string, List<Symbol>> Extensions = new Dictionary<string, List<Symbol>>();
    // Global type names (names that refer to types in IL; local variable names must not shadow them)
    public HashSet<string> TypeIlNames = new HashSet<string>();

    public void Error(string code, string message, Node at)
    {
        Diags.Error(code, message, at.File, at.Pos);
    }
}

public sealed class Declarer
{
    private readonly Compilation _c;
    private Dictionary<string, string>? _aliases;

    public Declarer(Compilation c)
    {
        _c = c;
    }

    // global usings of all files (excluding aliases)
    public List<string> GlobalUsings = new List<string>();

    public void DeclareAll()
    {
        foreach (var unit in _c.Units)
        {
            if (unit.List == null) continue;
            foreach (var n in unit.List)
            {
                if (n.Kind == NodeKind.Using && n.Flags == 1 && n.D == null)
                {
                    string u = n.Value == 1 ? StaticUsingPrefix + n.Text : n.Text;
                    if (!GlobalUsings.Contains(u)) GlobalUsings.Add(u);
                }
            }
        }
        for (int i = 0; i < _c.Units.Count; i++)
        {
            var unit = _c.Units[i];
            var usings = new List<string>();
            var levels = new List<string>();
            CollectUsings(unit, usings, levels, "");
            foreach (var g in GlobalUsings)
            {
                if (usings.Contains(g)) continue;
                usings.Add(g);
                levels.Add("");
            }
            _aliases = CollectAliases(unit, null);
            CollectMembers(unit, "", usings, levels, null);
        }
        foreach (var t in _c.Types) DeclareHeader(t);
        foreach (var t in _c.Types) DeclareMembers(t);
        foreach (var t in _c.Types) AssignIlNames(t);
        CheckStructCycles();
    }

    public const string StaticUsingPrefix = "static:";

    // Types imported with using static (names that are not found are skipped)
    public List<TypeDeclSym> StaticUsingTypes(TypeScope scope)
    {
        var result = new List<TypeDeclSym>();
        foreach (var u in scope.Usings)
        {
            if (!u.StartsWith(StaticUsingPrefix)) continue;
            string name = u.Replace(StaticUsingPrefix, "");
            if (_c.TypesByFullName.TryGetValue(name, out var t)) result.Add(t);
        }
        return result;
    }

    public static void CollectUsings(Node container, List<string> usings, List<string> levels, string level)
    {
        if (container.List == null) return;
        foreach (var n in container.List)
        {
            if (n.Kind == NodeKind.Using && n.D == null)
            {
                // using static entries go in the same list with a "static:" prefix (so they do not match namespace usings)
                usings.Add(n.Value == 1 ? StaticUsingPrefix + n.Text : n.Text);
                levels.Add(level);
            }
        }
    }

    // Alias of using X = A.B; (the alias Name in the Using's D)
    public Dictionary<string, string>? CollectAliases(Node container, Dictionary<string, string>? outer)
    {
        Dictionary<string, string>? result = outer;
        if (container.List == null) return result;
        bool copied = false;
        foreach (var n in container.List)
        {
            if (n.Kind != NodeKind.Using || n.D == null) continue;
            if (!copied)
            {
                copied = true;
                var copy = new Dictionary<string, string>();
                if (outer != null)
                {
                    foreach (var kv in outer) copy[kv.Key] = kv.Value;
                }
                result = copy;
            }
            result![n.D.Text] = n.Text;
        }
        return result;
    }

    private void AddNamespace(string ns)
    {
        if (ns == "") return;
        var parts = ns.Split('.');
        string acc = "";
        foreach (var p in parts)
        {
            acc = acc == "" ? p : acc + "." + p;
            _c.Namespaces.Add(acc);
        }
    }

    private void CollectMembers(Node container, string ns, List<string> usings, List<string> levels, TypeDeclSym? outer)
    {
        if (container.List == null) return;
        foreach (var n in container.List)
        {
            if (n.Kind == NodeKind.Namespace)
            {
                string inner = ns == "" ? n.Text : ns + "." + n.Text;
                AddNamespace(inner);
                var innerUsings = new List<string>();
                foreach (var u in usings) innerUsings.Add(u);
                var innerLevels = new List<string>();
                foreach (var l in levels) innerLevels.Add(l);
                CollectUsings(n, innerUsings, innerLevels, inner);
                var saveAliases = _aliases;
                _aliases = CollectAliases(n, _aliases);
                CollectMembers(n, inner, innerUsings, innerLevels, null);
                _aliases = saveAliases;
            }
            else if (n.Kind == NodeKind.TypeDecl && n.Text != "")
            {
                DeclareType(n, ns, usings, levels, outer);
            }
        }
    }

    private void DeclareType(Node n, string ns, List<string> usings, List<string> levels, TypeDeclSym? outer)
    {
        string full;
        if (outer != null) full = outer.FullName + "." + n.Text;
        else full = ns == "" ? n.Text : ns + "." + n.Text;
        // System.ValueTuple (TinySystem/ValueTuple.cs) is a separate type per type argument count, and the IL type names are separate too (System_ValueTuple2 etc.)
        if (n.File.IsLibrary && full == "System.ValueTuple" && n.List2 != null) full = full + n.List2.Count;
        var t = new TypeDeclSym(n.Text, full, ns, n.Value, n);
        n.TypeDecl = t;
        t.Outer = outer;
        t.IsStatic = n.Has(Mods.Static);
        t.IsRef = n.File.IsRef;
        t.Order = _c.Types.Count;
        if (n.List2 != null)
        {
            foreach (var tp in n.List2) t.TypeParams.Add(tp.Text);
        }
        if (_c.TypesByFullName.ContainsKey(full))
        {
            if (n.Has(Mods.Partial)) _c.Error("TCS1001", "unsupported syntax: PartialTypeDeclaration", n);
            // Types with the same name but a different number of type arguments are separate types in C#, but the self-host looks up by name, so they are not handled
            else if (_c.TypesByFullName[full].TypeParams.Count != t.TypeParams.Count) _c.Error("TCS1001", "unsupported syntax: GenericArityOverload(" + t.Name + ")", n);
            else _c.Error("CS0101", "The namespace '" + ns + "' already contains a definition for '" + n.Text + "'", n);
            return;
        }
        if (n.Has(Mods.Partial)) _c.Error("TCS1001", "unsupported syntax: PartialTypeDeclaration", n);
        _c.TypesByFullName[full] = t;
        _c.TypeIlNames.Add(t.IlName);
        _c.Types.Add(t);
        if (outer != null) outer.Nested.Add(t);
        var scope = new TypeScope(n.File, ns, usings);
        scope.UsingLevels = levels;
        scope.Type = t;
        scope.Aliases = _aliases;
        t.Scope = scope;
        if (n.D != null && n.D.List != null)
        {
            foreach (var a in n.D.List)
            {
                if (a.Text == "Flags" || a.Text == "System.Flags" || a.Text == "FlagsAttribute") t.IsFlags = true;
            }
        }
        // Nested types
        if (n.List != null)
        {
            foreach (var m in n.List)
            {
                if (m.Kind == NodeKind.TypeDecl && m.Text != "") DeclareType(m, ns, usings, levels, t);
            }
        }
    }

    public TypeScope ScopeOf(TypeDeclSym t) => t.Scope!;

    private void DeclareHeader(TypeDeclSym t)
    {
        var scope = ScopeOf(t);
        var n = t.Decl;
        t.TypeParamConstraints = ResolveConstraints(n.List2, scope);
        t.TypeParamClass = ClassFlags(n.List2);
        if (t.IsEnum)
        {
            t.EnumBase = TypeSym.IntType;
            if (n.List3 != null && n.List3.Count > 0) t.EnumBase = ResolveType(n.List3[0], scope);
            return;
        }
        if (n.List3 != null)
        {
            foreach (var b in n.List3)
            {
                // Attribute declarations from --ref (: Attribute). Attributes are used only for name resolution and emit no IL
                if (t.IsRef && b.Kind == NodeKind.TypeName && b.Text == "Attribute" && (b.A == null || b.A.Text == "System"))
                {
                    t.IsAttribute = true;
                    continue;
                }
                var bt = ResolveType(b, scope);
                if (t.IsRef && bt.Decl != null && !bt.Decl.IsInterface && !bt.Decl.IsEnum && t.BaseClass == null)
                {
                    // Inheritance of --ref host data classes (host declarations are not diagnosed)
                    t.BaseClass = bt.Decl;
                    continue;
                }
                if (bt.Kind == TypeKind.Interface) t.Interfaces.Add(bt);
                // `: object` is the same as writing no base
                else if (bt.Kind == TypeKind.Object && !t.IsInterface) continue;
                // User attribute classes (inheriting System.Attribute)
                else if (bt.IsBcl && bt.BclKey == "System.Attribute" && !t.IsInterface)
                    _c.Error("TCS1001", "unsupported syntax: ClassInheritance(System.Attribute)", b);
                else if (!bt.IsError)
                {
                    if (t.IsInterface) _c.Error("CS0527", "Type '" + bt.Display() + "' in interface list is not an interface", b);
                    else _c.Error("TCS1001", "unsupported syntax: ClassInheritance(" + bt.FullDisplay() + ")", b);
                }
            }
        }
    }

    // ---- Type resolution ----

    // Record the resolved type on the type syntax node (generic monomorphization looks for the positions of type references)
    public TypeSym ResolveType(Node n, TypeScope scope)
    {
        var t = ResolveTypeCore(n, scope);
        if (n.Oblivious && !t.Oblivious && !t.IsError)
        {
            t = t.WithAnnotation();
            t.Annotated = false;
            t.Oblivious = true;
        }
        if (n.Kind == NodeKind.TypeName || n.Kind == NodeKind.TupleType) n.Type = t;
        return t;
    }

    private TypeSym ResolveTypeCore(Node n, TypeScope scope)
    {
        switch (n.Kind)
        {
            case NodeKind.PredefType:
                return Predefined(n.Text, n);
            case NodeKind.ArrayType:
                if (n.Value == 2) _c.Error("TCS1001", "unsupported syntax: MultiDimensionalArray", n);
                return TypeSym.ArrayOf(ResolveType(n.A!, scope));
            case NodeKind.NullableType:
                {
                    var inner = ResolveType(n.A!, scope);
                    if (inner.IsValueType && inner.Kind != TypeKind.Nullable) return TypeSym.NullableOf(inner);
                    if (inner.Kind == TypeKind.TypeParam)
                    {
                        // T? with where T : struct is Nullable<T>; any other T? is only an annotation
                        if (inner.IsStructParam) return TypeSym.NullableOf(inner);
                        return inner.WithAnnotation();
                    }
                    if (inner.IsError) return inner;
                    return inner.WithAnnotation();
                }
            case NodeKind.TupleType:
                {
                    // Reject tuples with 8 or more elements (reported only once)
                    if (n.Count > 7 && n.Value == 0 && !n.File.IsRef && !n.File.IsLibrary)
                    {
                        n.Value = 1;
                        _c.Error("TCS1001", "unsupported syntax: TupleArity", n);
                    }
                    var t = new TypeSym(TypeKind.Tuple, "tuple");
                    t.Args = new List<TypeSym>();
                    t.Names = new List<string>();
                    foreach (var e in n.List!)
                    {
                        t.Args.Add(ResolveType(e.A!, scope));
                        t.Names.Add(e.Text);
                    }
                    return t;
                }
            case NodeKind.TypeName:
                return ResolveTypeName(n, scope);
        }
        _c.Error("CS0246", "The type or namespace name could not be found", n);
        return TypeSym.ErrorType;
    }

    public TypeSym Predefined(string name, Node at)
    {
        switch (name)
        {
            case "int": return TypeSym.IntType;
            case "float": return TypeSym.FloatType;
            case "bool": return TypeSym.BoolType;
            case "string": return TypeSym.StringType;
            case "char": return TypeSym.CharType;
            case "byte": return TypeSym.ByteType;
            case "void": return TypeSym.VoidType;
            case "object": return TypeSym.ObjectType;
            case "uint": return TypeSym.UIntType;
            case "short": return TypeSym.ShortType;
            case "sbyte": return TypeSym.SByteType;
            case "ushort": return TypeSym.UShortType;
            case "double":
                _c.Error("TCS1001", "unsupported syntax: DoubleType (numbers are int and float)", at);
                return TypeSym.DoubleType;
            case "long":
            case "ulong":
                _c.Error("TCS1001", "unsupported syntax: LongType (numbers are int and float)", at);
                return TypeSym.LongType;
            case "?":
                return TypeSym.ErrorType;
            case "decimal":
                _c.Error("TCS1001", "unsupported syntax: DecimalType", at);
                return TypeSym.ErrorType;
        }
        _c.Error("TCS1002", "unsupported API: type '" + name + "'", at);
        return TypeSym.ErrorType;
    }

    private List<TypeSym>? TypeArgs(Node n, TypeScope scope)
    {
        if (n.List == null) return null;
        var args = new List<TypeSym>();
        foreach (var a in n.List) args.Add(ResolveType(a, scope));
        return args;
    }

    private TypeSym ResolveTypeName(Node n, TypeScope scope)
    {
        var args = TypeArgs(n, scope);
        int arity = args == null ? 0 : args.Count;
        if (n.A != null)
        {
            // Qualified name: the outer part is a namespace or a type
            string? qual = QualifiedText(n.A);
            if (qual != null)
            {
                TypeDeclSym? decl;
                if (IsRooted(n)) _c.TypesByFullName.TryGetValue(qual + "." + n.Text, out decl);
                else decl = FindTypeInNamespaceOrType(qual, n.Text, scope);
                if (decl != null) return Instantiate(decl, args, n);
                if (IsLibraryNamespace(qual))
                {
                    var bt = BuiltinType(n.Text, args, n, qual);
                    if (bt != null) return bt;
                }
                string qualKey = qual + "." + BclMetadataName(n.Text, arity);
                if (BclCatalog.TypeDisplay(qualKey) != null) return BclTypeMention(qualKey, n);
            }
            var outerType = ResolveType(n.A, scope);
            if (outerType.Kind == TypeKind.Dict && (n.Text == "KeyCollection" || n.Text == "ValueCollection"))
            {
                var kc = new TypeSym(n.Text == "KeyCollection" ? TypeKind.KeyCollection : TypeKind.ValueCollection, n.Text);
                kc.Args = outerType.Args;
                return kc;
            }
            if (outerType.Decl != null)
            {
                foreach (var nested in outerType.Decl.Nested)
                {
                    if (nested.Name == n.Text) return Instantiate(nested, args, n);
                }
            }
            _c.Error("CS0246", "The type or namespace name '" + n.Text + "' could not be found", n);
            return TypeSym.ErrorType;
        }
        if (n.Value == 1)
        {
            if (_c.TypesByFullName.TryGetValue(n.Text, out var rootType) && rootType.Outer == null) return Instantiate(rootType, args, n);
            var rootBuiltin = BuiltinType(n.Text, args, n, null);
            if (rootBuiltin != null) return rootBuiltin;
            _c.Error("CS0400", "The type or namespace name '" + n.Text + "' could not be found in the global namespace", n);
            return TypeSym.ErrorType;
        }
        var found = LookupTypeByName(n.Text, arity, scope);
        if (found != null)
        {
            if (found.Kind == TypeKind.TypeParam) return found;
            if (found.Decl != null) return Instantiate(found.Decl, args, n);
            return found;
        }
        // Only TinySystem.Random is mapped. The Random visible with only using System is System.Random
        if (n.Text == "Random" && arity == 0 && !scope.Usings.Contains("TinySystem") && scope.Usings.Contains("System"))
        {
            _c.Error("TCS1002", "unsupported API: System.Random (use TinySystem.Random)", n);
            return TypeSym.ErrorType;
        }
        var builtin = BuiltinVisible(n.Text, scope) ? BuiltinType(n.Text, args, n, null) : null;
        if (builtin != null) return builtin;
        string? bclKey = BclTypeKey(n.Text, arity, scope);
        if (bclKey != null) return BclTypeMention(bclKey, n);
        _c.Error("CS0246", "The type or namespace name '" + n.Text + "' could not be found (are you missing a using directive or an assembly reference?)", n);
        return TypeSym.ErrorType;
    }

    // BCL and TinySystem namespaces (they can qualify built-in types)
    public static bool IsLibraryNamespace(string ns)
    {
        return ns == "System" || ns.StartsWith("System.") || ns == "TinySystem" || ns.StartsWith("TinySystem.");
    }

    // Whether it is a qualified name starting with global::
    private static bool IsRooted(Node n)
    {
        var cur = n;
        while (cur.A != null) cur = cur.A;
        return cur.Value == 1;
    }

    private string? QualifiedText(Node n)
    {
        if (n.Kind == NodeKind.TypeName && n.List == null)
        {
            if (n.A == null) return n.Text;
            var q = QualifiedText(n.A);
            if (q == null) return null;
            return q + "." + n.Text;
        }
        return null;
    }

    private TypeDeclSym? FindTypeInNamespaceOrType(string qual, string name, TypeScope scope)
    {
        // If the head of the qualification is a using alias, replace it with the alias target (namespace or type)
        if (scope.Aliases != null)
        {
            var parts = qual.Split('.');
            if (scope.Aliases.TryGetValue(parts[0], out var target))
            {
                parts[0] = target;
                qual = string.Join(".", parts);
            }
        }
        if (_c.TypesByFullName.TryGetValue(qual + "." + name, out var t)) return t;
        // Relative to the current namespace
        string ns = scope.Namespace;
        while (ns != "")
        {
            if (_c.TypesByFullName.TryGetValue(ns + "." + qual + "." + name, out var t2)) return t2;
            ns = ParentNamespace(ns);
        }
        return null;
    }

    public static string ParentNamespace(string ns)
    {
        var parts = ns.Split('.');
        if (parts.Length <= 1) return "";
        var keep = new List<string>();
        for (int i = 0; i < parts.Length - 1; i++) keep.Add(parts[i]);
        return string.Join(".", keep);
    }

    // Look up a type by name: in the order type arguments, nested types of enclosing types, namespace, usings
    // Last part of a BCL type's metadata name (`LinkedList`1`)
    public static string BclMetadataName(string name, int arity) => arity == 0 ? name : name + "`" + arity;

    // Metadata name of the BCL type visible through the used namespaces (null if none)
    public string? BclTypeKey(string name, int arity, TypeScope scope)
    {
        string meta = BclMetadataName(name, arity);
        foreach (var u in scope.Usings)
        {
            if (BclCatalog.TypeDisplay(u + "." + meta) != null) return u + "." + meta;
        }
        return null;
    }

    // BCL types that may appear only in type tests (members are TCS1002)
    private static bool IsTypeCheckOnlyBcl(string key)
    {
        switch (key)
        {
            case "System.Attribute":
            case "System.Type":
            case "System.Collections.Generic.IEnumerable`1":
            case "System.Collections.Generic.ICollection`1":
            case "System.Collections.Generic.IList`1":
            case "System.Collections.Generic.IDictionary`2":
            case "System.Collections.Generic.IReadOnlyList`1":
            case "System.Collections.Generic.IReadOnlyCollection`1":
                return true;
        }
        return false;
    }

    // BCL type at the place where the type is written. Types not in the subset are TCS1002
    private TypeSym BclTypeMention(string key, Node at)
    {
        // The System.ValueTuple name is written with tuple syntax
        if (key.StartsWith("System.ValueTuple"))
        {
            _c.Error("TCS1001", "unsupported syntax: ValueTupleName", at);
            return TypeSym.ErrorType;
        }
        if (!IsTypeCheckOnlyBcl(key)) _c.Error("TCS1002", "unsupported API: " + BclCatalog.TypeDisplay(key), at);
        return TypeSym.Bcl(key);
    }

    // Look up by name and type argument count (`Queue` and `Queue<T>` are different types with the same name)
    private TypeDeclSym? TypeByFullName(string full, int arity)
    {
        if (!_c.TypesByFullName.TryGetValue(full, out var t)) return null;
        if (t.TypeParams.Count == arity) return t;
        foreach (var other in _c.Types)
        {
            if (other.FullName == full && other.TypeParams.Count == arity) return other;
        }
        return t;
    }

    public TypeSym? LookupTypeByName(string name, int arity, TypeScope scope)
    {
        if (scope.MethodTypeParams != null)
        {
            for (int i = 0; i < scope.MethodTypeParams.Count; i++)
            {
                if (scope.MethodTypeParams[i] == name && arity == 0)
                {
                    var tp = new TypeSym(TypeKind.TypeParam, name);
                    tp.Index = i;
                    tp.IsMethodParam = true;
                    tp.IsStructParam = scope.MethodTypeParamStruct != null && i < scope.MethodTypeParamStruct.Count && scope.MethodTypeParamStruct[i];
                    return tp;
                }
            }
        }
        var cur = scope.Type;
        while (cur != null)
        {
            for (int i = 0; i < cur.TypeParams.Count; i++)
            {
                if (cur.TypeParams[i] == name && arity == 0)
                {
                    if (cur != scope.Type) break;
                    var tp = new TypeSym(TypeKind.TypeParam, name);
                    tp.Index = i;
                    tp.IsStructParam = cur.Decl.List2 != null && i < cur.Decl.List2.Count && (cur.Decl.List2[i].Value & 2) != 0;
                    return tp;
                }
            }
            if (cur.Name == name && cur.TypeParams.Count == arity) return TypeSym.OfDecl(cur, null);
            foreach (var nested in cur.Nested)
            {
                if (nested.Name == name && nested.TypeParams.Count == arity) return TypeSym.OfDecl(nested, null);
            }
            cur = cur.Outer;
        }
        if (scope.Aliases != null && arity == 0 && scope.Aliases.TryGetValue(name, out var target))
        {
            var at = ResolveAlias(target);
            if (at != null) return at;
        }
        string ns = scope.Namespace;
        while (true)
        {
            string full = ns == "" ? name : ns + "." + name;
            var t = TypeByFullName(full, arity);
            if (t != null && t.Outer == null) return TypeSym.OfDecl(t, null);
            if (ns == "") break;
            ns = ParentNamespace(ns);
        }
        foreach (var u in scope.Usings)
        {
            var t = TypeByFullName(u + "." + name, arity);
            if (t != null && t.Outer == null) return TypeSym.OfDecl(t, null);
        }
        // Nested types of using static types
        foreach (var st in StaticUsingTypes(scope))
        {
            foreach (var nested in st.Nested)
            {
                if (nested.Name == name && nested.TypeParams.Count == arity) return TypeSym.OfDecl(nested, null);
            }
        }
        return null;
    }

    private TypeSym Instantiate(TypeDeclSym decl, List<TypeSym>? args, Node at)
    {
        int arity = args == null ? 0 : args.Count;
        if (arity != decl.TypeParams.Count)
        {
            _c.Error("CS0305", "Using the generic type '" + decl.Name + "' requires " + decl.TypeParams.Count + " type arguments", at);
            return TypeSym.ErrorType;
        }
        // Users of a delegate declaration (outside the subset; the declaration is TCS1001) treat it as the delegate type of its signature
        if (decl.Decl.Has(Mods.Delegate) && decl.Decl.C != null && decl.Scope != null) return DelegateOf(decl, args);
        return TypeSym.OfDecl(decl, args);
    }

    private TypeSym DelegateOf(TypeDeclSym decl, List<TypeSym>? args)
    {
        var ps = new List<TypeSym>();
        if (decl.Decl.B != null && decl.Decl.B.List != null)
        {
            foreach (var p in decl.Decl.B.List)
            {
                var pt = p.A != null ? ResolveType(p.A, decl.Scope!) : TypeSym.ErrorType;
                ps.Add(args != null ? pt.Subst(args, null) : pt);
            }
        }
        var ret = ResolveType(decl.Decl.C!, decl.Scope!);
        return TypeSym.Delegate(ps, args != null ? ret.Subst(args, null) : ret);
    }

    // Target of a using alias (a user type, or a BCL / TinySystem type)
    public TypeSym? ResolveAlias(string target)
    {
        if (_c.TypesByFullName.TryGetValue(target, out var t)) return TypeSym.OfDecl(t, null);
        var parts = target.Split('.');
        string last = parts[parts.Length - 1];
        string qual = ParentNamespace(target);
        // TinySystem facade (`using M = TinySystem.Math;`)
        if (qual == "TinySystem" && (last == "Math" || last == "String" || last == "List" || last == "Dict"))
            return new TypeSym(TypeKind.StaticRef, target);
        if (IsLibraryNamespace(qual))
        {
            var st = Builtins.StaticType(last);
            if (st != null) return st;
            var dummy = new Node(NodeKind.Name, _c.Files[0], 0);
            return BuiltinType(last, null, dummy, qual);
        }
        return null;
    }

    // An unqualified built-in type name is visible only when its namespace is used or the code is written inside that namespace
    public static bool BuiltinVisible(string name, TypeScope scope)
    {
        switch (name)
        {
            case "List":
            case "Dictionary":
            case "HashSet":
            case "Queue":
            case "KeyValuePair":
                return NamespaceVisible("System.Collections.Generic", scope);
            case "Utf8Bytes":
            case "Runes":
                return NamespaceVisible("TinySystem.Text", scope);
            case "Rune":
                return NamespaceVisible("System.Text", scope);
            case "Random":
                return NamespaceVisible("TinySystem", scope);
            case "Math":
                return NamespaceVisible("System", scope) || NamespaceVisible("TinySystem", scope);
        }
        return NamespaceVisible("System", scope);
    }

    public static bool NamespaceVisible(string ns, TypeScope scope)
    {
        string cur = scope.Namespace;
        while (cur != "")
        {
            if (cur == ns) return true;
            cur = ParentNamespace(cur);
        }
        foreach (var u in scope.Usings)
        {
            if (u == ns) return true;
        }
        return false;
    }

    // Built-in types (BCL and TinySystem)
    public TypeSym? BuiltinType(string name, List<TypeSym>? args, Node at, string? qual)
    {
        int arity = args == null ? 0 : args.Count;
        switch (name)
        {
            case "List":
                if (arity == 1) return TypeSym.ListOf(args![0]);
                break;
            case "Dictionary":
                if (arity == 2) return TypeSym.DictOf(args![0], args[1]);
                break;
            case "HashSet":
                if (arity == 1) return TypeSym.Generic(TypeKind.HashSet, "HashSet", args![0]);
                break;
            case "Queue":
                if (arity == 1) return TypeSym.Generic(TypeKind.Queue, "Queue", args![0]);
                break;
            case "KeyValuePair":
                if (arity == 2) return TypeSym.Generic2(TypeKind.KeyValuePair, "KeyValuePair", args![0], args[1]);
                break;
            case "Action":
                return TypeSym.Delegate(args == null ? new List<TypeSym>() : args, TypeSym.VoidType);
            case "Func":
                if (arity >= 1)
                {
                    var ps = new List<TypeSym>();
                    for (int i = 0; i < arity - 1; i++) ps.Add(args![i]);
                    return TypeSym.Delegate(ps, args![arity - 1]);
                }
                break;
            case "Comparison":
                if (arity == 1)
                {
                    var ps = new List<TypeSym>();
                    ps.Add(args![0]);
                    ps.Add(args[0]);
                    return TypeSym.Delegate(ps, TypeSym.IntType);
                }
                break;
            case "Predicate":
                if (arity == 1)
                {
                    var ps = new List<TypeSym>();
                    ps.Add(args![0]);
                    return TypeSym.Delegate(ps, TypeSym.BoolType);
                }
                break;
            case "Utf8Bytes":
                if (arity == 0) return TypeSym.Utf8BytesType;
                break;
            case "Rune":
                if (arity == 0) return TypeSym.RuneType;
                break;
            case "Random":
                // System.Random is not TinySystem.Random
                if (arity == 0 && qual != "System") return TypeSym.RandomType;
                break;
            case "String":
                if (arity == 0) return TypeSym.StringType;
                break;
            case "Object":
                if (arity == 0) return TypeSym.ObjectType;
                break;
            case "Int32":
                if (arity == 0) return TypeSym.IntType;
                break;
            case "Single":
                if (arity == 0) return TypeSym.FloatType;
                break;
            case "Boolean":
                if (arity == 0) return TypeSym.BoolType;
                break;
            case "Char":
                if (arity == 0) return TypeSym.CharType;
                break;
        }
        return null;
    }

    // ---- Member declarations ----

    private void DeclareMembers(TypeDeclSym t)
    {
        var scope = ScopeOf(t);
        var n = t.Decl;
        if (t.IsEnum)
        {
            int next = 0;
            if (n.List != null)
            {
                foreach (var em in n.List)
                {
                    var s = new Symbol(SymKind.EnumMember, em.Text, TypeSym.OfDecl(t, null));
                    s.Owner = t;
                    s.IsStatic = true;
                    s.IsConst = true;
                    s.Decl = em;
                    em.Sym = s;
                    // Binder folds the value as a constant. Without an initializer, it is the previous value + 1
                    s.ConstState = em.A == null ? 0 : -1;
                    if (em.A == null) s.ConstVal = null;
                    em.Value = next;
                    t.AddMember(s);
                }
            }
            return;
        }
        if (t.IsRecord && n.A != null)
        {
            t.RecordParams = new List<ParamSym>();
            var positional = n.A.List != null ? n.A.List : new List<Node>();
            foreach (var p in positional)
            {
                var pt = ResolveType(p.A!, scope);
                var ps = new ParamSym(p.Text, pt);
                ps.Default = p.B;
                t.RecordParams.Add(ps);
                var prop = new Symbol(SymKind.Property, p.Text, pt);
                prop.Owner = t;
                prop.HasGetter = true;
                // Positional properties of a (non-readonly) record struct are writable
                prop.HasSetter = t.DeclKind == DeclKinds.RecordStruct && !n.Has(Mods.Readonly);
                prop.IsInit = true;
                prop.IsAuto = true;
                prop.Decl = p;
                t.AddMember(prop);
            }
            var ctor = new Symbol(SymKind.Ctor, ".ctor", TypeSym.VoidType);
            ctor.Owner = t;
            ctor.Params = t.RecordParams;
            ctor.Decl = n.A;
            t.Ctors.Add(ctor);
        }
        if (n.List == null) return;
        foreach (var m in n.List)
        {
            switch (m.Kind)
            {
                case NodeKind.Field:
                    {
                        var ft = ResolveType(m.A!, scope);
                        foreach (var v in m.List!)
                        {
                            var s = new Symbol(SymKind.Field, v.Text, ft);
                            s.Owner = t;
                            s.IsStatic = m.Has(Mods.Static) || m.Has(Mods.Const);
                            s.IsConst = m.Has(Mods.Const);
                            s.IsReadonly = m.Has(Mods.Readonly) || s.IsConst;
                            s.Decl = v;
                            v.Sym = s;
                            if (s.IsConst) s.ConstState = -1;
                            CheckDuplicate(t, s, v);
                            t.AddMember(s);
                        }
                        break;
                    }
                case NodeKind.Property:
                    {
                        var pt = ResolveType(m.A!, scope);
                        var s = new Symbol(SymKind.Property, m.Text, pt);
                        s.Owner = t;
                        s.IsStatic = m.Has(Mods.Static);
                        s.Decl = m;
                        m.Sym = s;
                        s.HasGetter = m.B != null;
                        s.HasSetter = m.C != null && m.C.Text != "init";
                        s.IsInit = m.C != null && m.C.Text == "init";
                        bool getAuto = m.B != null && m.B.A == null && m.B.B == null;
                        bool setAuto = m.C == null || m.C.A == null && m.C.B == null;
                        s.IsAuto = getAuto && setAuto && !t.IsInterface;
                        if (t.IsInterface) s.IsAuto = false;
                        CheckDuplicate(t, s, m);
                        t.AddMember(s);
                        break;
                    }
                case NodeKind.Method:
                    {
                        var s = new Symbol(SymKind.Method, m.Text, TypeSym.VoidType);
                        s.Owner = t;
                        s.IsStatic = m.Has(Mods.Static);
                        s.Decl = m;
                        m.Sym = s;
                        if (m.List2 != null)
                        {
                            s.TypeParams = new List<string>();
                            foreach (var tp in m.List2) s.TypeParams.Add(tp.Text);
                        }
                        List<bool>? structFlags = null;
                        if (m.List2 != null)
                        {
                            structFlags = new List<bool>();
                            foreach (var tpn in m.List2) structFlags.Add((tpn.Value & 2) != 0);
                        }
                        var mscope = scope.WithMethodTypeParams(s.TypeParams, structFlags);
                        if (s.TypeParams != null)
                        {
                            s.TypeParamConstraints = ResolveConstraints(m.List2, mscope);
                            s.TypeParamClass = ClassFlags(m.List2);
                        }
                        s.Type = ResolveType(m.A!, mscope);
                        s.Params = Params(m, mscope);
                        if (s.Params.Count > 0 && s.Params[0].IsThis)
                        {
                            s.IsExtension = true;
                            if (!_c.Extensions.TryGetValue(s.Name, out var list))
                            {
                                list = new List<Symbol>();
                                _c.Extensions[s.Name] = list;
                            }
                            list.Add(s);
                        }
                        // --ref host declarations are not diagnosed
                        // override / virtual / abstract are reported by the syntax compliance check (Compliance)
                        var sameName = t.Lookup(s.Name);
                        if (sameName != null)
                        {
                            foreach (var other in sameName)
                            {
                                // An override inherits the base signature (including type argument constraints), so it is not compared here (override is outside the subset)
                                if (other.Kind == SymKind.Method && SameParams(other, s) && !m.Has(Mods.Override) && !(other.Decl != null && other.Decl.Has(Mods.Override)))
                                {
                                    _c.Error("CS0111", "Type '" + t.Name + "' already defines a member called '" + s.Name + "' with the same parameter types", m);
                                    break;
                                }
                            }
                        }
                        t.AddMember(s);
                        break;
                    }
                case NodeKind.Ctor:
                    {
                        var s = new Symbol(SymKind.Ctor, ".ctor", TypeSym.VoidType);
                        s.Owner = t;
                        s.IsStatic = m.Has(Mods.Static);
                        s.Decl = m;
                        m.Sym = s;
                        s.Params = Params(m, scope);
                        if (s.IsStatic)
                        {
                            _c.Error("TCS1001", "unsupported syntax: StaticConstructor", m);
                            break;
                        }
                        foreach (var other in t.Ctors)
                        {
                            if (SameParams(other, s))
                            {
                                _c.Error("CS0111", "Type '" + t.Name + "' already defines a member called '" + t.Name + "' with the same parameter types", m);
                                break;
                            }
                        }
                        t.Ctors.Add(s);
                        break;
                    }
                case NodeKind.OperatorDecl:
                    {
                        var s = new Symbol(SymKind.Method, "op_" + OperatorName(m.Text, m.Count), TypeSym.VoidType);
                        s.Owner = t;
                        s.IsStatic = true;
                        s.Decl = m;
                        m.Sym = s;
                        s.Type = ResolveType(m.A!, scope);
                        s.Params = Params(m, scope);
                        t.AddMember(s);
                        break;
                    }
            }
        }
    }

    // Marker for where T : class (the parser puts it in bit 1 of the type argument's Value)
    private static List<bool> ClassFlags(List<Node>? typeParams)
    {
        var result = new List<bool>();
        if (typeParams == null) return result;
        foreach (var tp in typeParams) result.Add((tp.Value & 1) != 0);
        return result;
    }

    private List<List<TypeSym>> ResolveConstraints(List<Node>? typeParams, TypeScope scope)
    {
        var result = new List<List<TypeSym>>();
        if (typeParams == null) return result;
        foreach (var tp in typeParams)
        {
            var list = new List<TypeSym>();
            if (tp.List != null)
            {
                foreach (var c in tp.List) list.Add(ResolveType(c, scope));
            }
            result.Add(list);
        }
        return result;
    }

    public static string OperatorName(string op, int arity)
    {
        switch (op)
        {
            case "+": return arity == 1 ? "UnaryPlus" : "Addition";
            case "-": return arity == 1 ? "UnaryNegation" : "Subtraction";
            case "*": return "Multiply";
            case "/": return "Division";
            case "%": return "Modulus";
            case "==": return "Equality";
            case "!=": return "Inequality";
            case "<": return "LessThan";
            case ">": return "GreaterThan";
            case "<=": return "LessThanOrEqual";
            case ">=": return "GreaterThanOrEqual";
            case "!": return "LogicalNot";
            case "~": return "OnesComplement";
            case "&": return "BitwiseAnd";
            case "|": return "BitwiseOr";
            case "^": return "ExclusiveOr";
            case "<<": return "LeftShift";
            case ">>": return "RightShift";
            case "++": return "Increment";
            case "--": return "Decrement";
            case "true": return "True";
            case "false": return "False";
        }
        return "Unknown";
    }

    // If a struct's instance fields (including auto property backing fields) contain the struct itself, the value size is undetermined (CS0523)
    private void CheckStructCycles()
    {
        foreach (var t in _c.Types)
        {
            if (!t.IsStruct) continue;
            foreach (var m in t.Members)
            {
                if (!IsInstanceStorage(m)) continue;
                var ft = StructOf(m.Type);
                if (ft != null && ReachesStruct(ft, t, new HashSet<TypeDeclSym>()))
                {
                    _c.Error("CS0523", "Struct member '" + t.Name + "." + m.Name + "' of type '" + m.Type.Display() + "' causes a cycle in the struct layout", m.Decl!);
                    break;
                }
            }
        }
    }

    private static bool IsInstanceStorage(Symbol m) =>
        !m.IsStatic && !m.IsConst && (m.Kind == SymKind.Field || m.Kind == SymKind.Property && m.IsAuto);

    private static TypeDeclSym? StructOf(TypeSym t)
    {
        if (t.Kind == TypeKind.Nullable) return StructOf(t.Elem);
        if (t.Decl != null && t.Decl.IsStruct) return t.Decl;
        return null;
    }

    private static bool ReachesStruct(TypeDeclSym from, TypeDeclSym target, HashSet<TypeDeclSym> visited)
    {
        if (from == target) return true;
        if (!visited.Add(from)) return false;
        foreach (var m in from.Members)
        {
            if (!IsInstanceStorage(m)) continue;
            var ft = StructOf(m.Type);
            if (ft != null && ReachesStruct(ft, target, visited)) return true;
        }
        return false;
    }

    // Whether the parameter type lists (and the method type argument counts) are the same
    private static bool SameParams(Symbol a, Symbol b)
    {
        int na = a.Params == null ? 0 : a.Params.Count;
        int nb = b.Params == null ? 0 : b.Params.Count;
        if (na != nb) return false;
        int ta = a.TypeParams == null ? 0 : a.TypeParams.Count;
        int tb = b.TypeParams == null ? 0 : b.TypeParams.Count;
        if (ta != tb) return false;
        for (int i = 0; i < na; i++)
        {
            if (!TypeSym.Same(a.Params![i].Type, b.Params![i].Type)) return false;
            if (a.Params[i].IsOut != b.Params[i].IsOut || a.Params[i].IsByRef != b.Params[i].IsByRef) return false;
        }
        return true;
    }

    private void CheckDuplicate(TypeDeclSym t, Symbol s, Node at)
    {
        var existing = t.Lookup(s.Name);
        if (existing != null)
            _c.Error("CS0102", "The type '" + t.Name + "' already contains a definition for '" + s.Name + "'", at);
    }

    private List<ParamSym> Params(Node m, TypeScope scope)
    {
        var result = new List<ParamSym>();
        if (m.List == null) return result;
        foreach (var p in m.List)
        {
            var pt = ResolveType(p.A!, scope);
            var ps = new ParamSym(p.Text, pt);
            ps.IsOut = p.Has(Mods.Out);
            ps.IsByRef = p.Has(Mods.Out) || p.Has(Mods.Ref) || p.Has(Mods.In);
            ps.IsThis = p.Has(Mods.This);
            ps.Default = p.B;
            if (p.Has(Mods.Ref)) _c.Error("TCS1001", "unsupported syntax: RefParameter", p);
            if (p.Has(Mods.In)) _c.Error("TCS1001", "unsupported syntax: InParameter", p);
            if (p.Has(Mods.Params)) _c.Error("TCS1001", "unsupported syntax: ParamsParameter", p);
            result.Add(ps);
        }
        return result;
    }

    // ---- IL names (IlName) ----

    private void AssignIlNames(TypeDeclSym t)
    {
        // If there are several methods with the same name, distinguish them by signature
        foreach (var m in t.Members)
        {
            if (m.Kind != SymKind.Method) continue;
            var same = t.Lookup(m.Name)!;
            int methods = 0;
            foreach (var o in same)
            {
                if (o.Kind == SymKind.Method) methods++;
            }
            m.IlName = methods > 1 ? m.Name + "__" + Signature(m) : m.Name;
        }
        // Constructor (struct new is the function that creates the default value, so the constructor is ctor)
        string ctorBase = t.IsStruct ? "ctor" : "new";
        if (t.Ctors.Count == 1) t.Ctors[0].IlName = ctorBase;
        else
        {
            foreach (var c in t.Ctors) c.IlName = ctorBase + "__" + Signature(c);
        }
        if (t.IsStruct)
        {
            bool immutable = t.Decl.Has(Mods.Readonly);
            if (!immutable)
            {
                immutable = true;
                foreach (var m in t.Members)
                {
                    if (m.IsStatic) continue;
                    if (m.Kind == SymKind.Field && !m.IsReadonly) immutable = false;
                    if (m.Kind == SymKind.Property && m.IsAuto && m.HasSetter) immutable = false;
                }
            }
            t.IsImmutable = immutable;
        }
        foreach (var m in t.Members)
        {
            if (m.Kind == SymKind.Field || m.Kind == SymKind.Property || m.Kind == SymKind.EnumMember) m.IlName = m.Name;
        }
        if (t.IsRef) AssignHostNames(t);
    }

    // Names of --ref types (host) are mapped by the host rules:
    // types join the namespace and nested types in all lowercase with . (Lub.Gfx → lub.gfx). Enums are placed flat under their containing type or namespace.
    // members are snake_case (BeginPass → begin_pass), and enum members are UPPER_SNAKE (Rgba8 → RGBA8)
    private static void AssignHostNames(TypeDeclSym t)
    {
        t.IlName = HostPath(t);
        foreach (var m in t.Members)
        {
            if (m.Kind == SymKind.EnumMember) m.IlName = HostNames.HostConst(m.Name);
            else m.IlName = HostNames.HostMember(m.Name);
        }
    }

    private static string HostPath(TypeDeclSym t)
    {
        var parts = new List<string>();
        TypeDeclSym? cur = t.IsEnum ? t.Outer : t;
        if (t.IsEnum && cur == null)
        {
            // An enum directly under a namespace is placed under the namespace
            if (t.Namespace == "") return "";
        }
        while (cur != null)
        {
            parts.Insert(0, HostNames.HostPathSegment(cur.Name));
            cur = cur.Outer;
        }
        if (t.Namespace != "")
        {
            var ns = t.Namespace.Split('.');
            for (int i = ns.Length - 1; i >= 0; i--) parts.Insert(0, HostNames.HostPathSegment(ns[i]));
        }
        return string.Join(".", parts);
    }

    public static string Signature(Symbol m)
    {
        var parts = new List<string>();
        if (m.Params != null)
        {
            foreach (var p in m.Params) parts.Add(SigName(p.Type));
        }
        if (parts.Count == 0) return "0";
        return string.Join("__", parts);
    }

    private static string SigName(TypeSym t)
    {
        switch (t.Kind)
        {
            case TypeKind.Array: return SigName(t.Elem) + "Array";
            case TypeKind.Nullable: return SigName(t.Elem) + "N";
            case TypeKind.Delegate:
                {
                    var parts = new List<string>();
                    parts.Add(t.Ret!.Kind == TypeKind.Void ? "Action" : "Func");
                    foreach (var a in t.Args!) parts.Add(SigName(a));
                    if (t.Ret.Kind != TypeKind.Void) parts.Add(SigName(t.Ret));
                    return string.Join("_", parts);
                }
        }
        string name = t.Decl != null ? t.Decl.IlName : t.Name;
        if (t.Args != null && t.Args.Count > 0)
        {
            var parts = new List<string>();
            parts.Add(name);
            foreach (var a in t.Args) parts.Add(SigName(a));
            return string.Join("_", parts);
        }
        return name;
    }
}

public static class HostNames
{
    // ---- host (--ref) names (lub-gen maps them with the same function) ----

    public static string HostMember(string name) => ToSnake(name);

    public static string HostConst(string name) => ToSnake(name).ToUpper();

    public static string HostPathSegment(string name) => name.ToLower();

    // A name with no lowercase letters (CLEAR / DEPTH24_STENCIL8) is treated as already all-uppercase snake_case and lowercased.
    // Otherwise, keep a leading _, insert _ before each uppercase letter from the second character on, and lowercase
    private static string ToSnake(string name)
    {
        var b = new Utf8Bytes(name);
        bool anyLower = false;
        for (int i = 0; i < b.Length; i++)
        {
            if (b[i] >= 'a' && b[i] <= 'z') anyLower = true;
        }
        if (!anyLower) return name.ToLower();
        var parts = new List<string>();
        int k = 0;
        while (k < b.Length && b[k] == '_')
        {
            parts.Add("_");
            k++;
        }
        bool first = true;
        int run = k;
        for (; k < b.Length; k++)
        {
            int c = b[k];
            if (c >= 'A' && c <= 'Z')
            {
                if (k > run) parts.Add(b.Slice(run, k - run).ToString());
                if (!first) parts.Add("_");
                parts.Add(b.Slice(k, 1).ToString().ToLower());
                run = k + 1;
            }
            first = false;
        }
        if (k > run) parts.Add(b.Slice(run, k - run).ToString());
        return string.Join("", parts);
    }
}
