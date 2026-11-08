using System.Collections.Generic;
using TinySystem.Text;

namespace TcsSelf;

// 構文だけで決まる準拠検査 (TCS1001)。準拠の規則はこの compiler だけが持ち、IDE の analyzer (TinyCs.Analyzers) も
// この compiler を動かして同じ診断を出す。名前は `unsupported syntax: <名前>` (Roslyn の構文の種類の名前に揃えたもの)。
// --ref の宣言と tcs が配る library は対象外
public sealed class Compliance
{
    private readonly Diagnostics _diags;
    private SourceFile? _file;

    // 生成コードが class table に置く key (ReservedClassKeys)
    private static readonly List<string> ReservedClassKeys = SplitWords("new ctor");
    // runtime が _G に置く名前 (ReservedRuntimeGlobals)
    public static readonly List<string> RuntimeGlobals = SplitWords("TinySystem List Dict Math String TcsQueue TcsHashSet");
    // member の名前に使えない語 (LuaKeywords)
    private static readonly List<string> LuaKeywords = SplitWords("and break do else elseif end false for function global goto if in local nil not or repeat return then true until while");
    private static readonly List<string> CallerInfoNames = SplitWords("CallerArgumentExpression CallerMemberName CallerLineNumber CallerFilePath");

    public Compliance(Diagnostics diags)
    {
        _diags = diags;
    }

    private static List<string> SplitWords(string s)
    {
        var result = new List<string>();
        foreach (var w in s.Split(' ')) result.Add(w);
        return result;
    }

    public void CheckUnit(Node unit)
    {
        _file = unit.File;
        if (unit.List != null)
        {
            foreach (var n in unit.List) Declaration(n, null, false);
        }
        if (unit.List2 != null)
        {
            foreach (var st in unit.List2) Walk(st);
        }
    }

    private void Report(string name, Node at)
    {
        _diags.Error("TCS1001", "unsupported syntax: " + name, at.File, at.Pos);
    }

    // 出力の段でだけ分かる診断 (参照実装は型の出力で報告する)。他に誤りが無いときだけ報告する
    public List<Diagnostic> Deferred = new List<Diagnostic>();

    private void ReportDeferred(string name, Node at)
    {
        Deferred.Add(new Diagnostic("TCS1001", "unsupported syntax: " + name, at.File, at.Pos, true));
    }

    // ---- 宣言 ----

    private void Declaration(Node n, Node? owner, bool inGeneric)
    {
        switch (n.Kind)
        {
            case NodeKind.Namespace:
                NamespaceName(n);
                if (n.List != null)
                {
                    foreach (var m in n.List) Declaration(m, null, false);
                }
                return;
            case NodeKind.TypeDecl:
                TypeDecl(n, owner, inGeneric);
                return;
        }
    }

    private void NamespaceName(Node n)
    {
        foreach (var part in n.Text.Split('.'))
        {
            string common = IdentifierRule(part);
            if (common != "")
            {
                Report(common, n);
                return;
            }
            if (part.Contains("_"))
            {
                Report("UnderscoreNamespaceName(" + part + ")", n);
                return;
            }
        }
    }

    private void TypeDecl(Node n, Node? owner, bool inGeneric)
    {
        if (n.Text == "") return;
        bool isInterface = n.Value == DeclKinds.Interface;
        string common = IdentifierRule(n.Text);
        if (common != "") Report(common, n);
        else if (n.Text.Contains("_")) Report("UnderscoreTypeName(" + n.Text + ")", n);
        else if (IsUnsafeLuaIdentifier(n.Text)) Report(UnsafeLuaIdentifierName(n.Text), n);
        else if (!isInterface && RuntimeGlobals.Contains(n.Text)) Report("RuntimeGlobalIdentifier(" + n.Text + ")", n);
        if (inGeneric) Report("NestedTypeInGenericType", n);
        if (n.Has(Mods.Abstract) && (n.Value == DeclKinds.Class || n.Value == DeclKinds.RecordClass)) Report("AbstractClass", n);
        // delegate の宣言の parameter (out は method の宣言にだけ書ける)
        if (n.Has(Mods.Delegate))
        {
            if (n.B != null) Params(n.B, false);
            return;
        }
        bool generic = inGeneric || n.Count2 > 0;
        // record の positional parameter は member になる
        if (n.A != null && n.A.List != null)
        {
            foreach (var p in n.A.List) MemberName(p.Text, p);
        }
        if (n.List == null) return;
        foreach (var m in n.List) Member(m, n, generic);
    }

    private void Member(Node m, Node owner, bool inGeneric)
    {
        if (m.Kind == NodeKind.TypeDecl)
        {
            TypeDecl(m, owner, inGeneric);
            return;
        }
        if (m.Kind == NodeKind.EnumMember)
        {
            MemberName(m.Text, m);
            return;
        }
        bool isClass = owner.Value == DeclKinds.Class || owner.Value == DeclKinds.RecordClass;
        bool isStruct = owner.Value == DeclKinds.Struct || owner.Value == DeclKinds.RecordStruct;
        if (m.Has(Mods.New)) Report("NewMemberHiding", m);
        else if ((isClass || isStruct) && !IsToStringOverride(m))
        {
            string inherit = InheritanceModifierName(m);
            if (inherit != "") Report(inherit, m);
        }
        if (isStruct && m.Kind == NodeKind.Ctor && !m.Has(Mods.Static) && m.Count == 0) Report("StructMember(ConstructorDeclaration)", m);
        // interface の field は止める
        if (owner.Value == DeclKinds.Interface && m.Kind == NodeKind.Field) Report("InterfaceField", m);
        // record class の本体に書けるのは method、演算子、static な field と auto property、型だけ (参照実装の VisitRecord)
        if (owner.Value == DeclKinds.RecordClass && !IsRecordClassMember(m)) ReportDeferred("member: " + RoslynMemberKind(m), m);
        Attributes(m.D);
        switch (m.Kind)
        {
            case NodeKind.Field:
                if (m.Has(Mods.Static) && !m.Has(Mods.Const) && inGeneric) Report("GenericStaticField", m);
                if (m.List != null)
                {
                    foreach (var v in m.List)
                    {
                        MemberName(v.Text, v);
                        if (v.A != null) Initializer(v.A);
                    }
                }
                return;
            case NodeKind.Property:
                MemberName(m.Text, m);
                // interface の実装付きの member (既定の実装) は止める
                if (owner.Value == DeclKinds.Interface && (!IsAutoAccessor(m.B) || !IsAutoAccessor(m.C) || m.D != null && m.D.Kind != NodeKind.Attribute))
                    Report("InterfaceDefaultMember", m);
                if (m.Has(Mods.Static) && inGeneric && IsAutoAccessor(m.B) && IsAutoAccessor(m.C)) Report("GenericStaticField", m);
                if (m.B != null) Walk(m.B);
                if (m.C != null) Walk(m.C);
                if (m.D != null && m.D.Kind != NodeKind.Attribute) Walk(m.D);
                return;
            case NodeKind.Method:
                MemberName(m.Text, m);
                if (owner.Value == DeclKinds.Interface && (m.B != null || m.C != null)) Report("InterfaceDefaultMember", m);
                Params(m, true);
                Body(m);
                return;
            case NodeKind.Ctor:
                if (m.D != null && m.D.Kind == NodeKind.Call && m.D.Text == "this") Report("ThisConstructorInitializer", m.D);
                Params(m, false);
                Body(m);
                return;
            case NodeKind.OperatorDecl:
                if (owner.Value == DeclKinds.Interface) Report("InterfaceOperatorDeclaration", m);
                else if (!IsSupportedOperator(m.Text, m.Count)) Report("OperatorDeclaration(" + m.Text + ")", m);
                Params(m, true);
                Body(m);
                return;
        }
    }

    private static bool IsRecordClassMember(Node m)
    {
        switch (m.Kind)
        {
            case NodeKind.Method:
                return true;
            case NodeKind.OperatorDecl:
                return IsSupportedOperator(m.Text, m.Count);
            case NodeKind.Field:
                return m.Has(Mods.Static) || m.Has(Mods.Const);
            case NodeKind.Property:
                return m.Has(Mods.Static) && IsAutoAccessor(m.B) && IsAutoAccessor(m.C);
        }
        return false;
    }

    private static string RoslynMemberKind(Node m)
    {
        switch (m.Kind)
        {
            case NodeKind.Field: return "FieldDeclaration";
            case NodeKind.Property: return "PropertyDeclaration";
            case NodeKind.Ctor: return "ConstructorDeclaration";
            case NodeKind.OperatorDecl: return "OperatorDeclaration";
        }
        return "MemberDeclaration";
    }

    private static bool IsAutoAccessor(Node? acc) => acc == null || acc.A == null && acc.B == null;

    // `public override string ToString()` (sealed は継承の語なので通さない)
    private static bool IsToStringOverride(Node m) =>
        m.Kind == NodeKind.Method && m.Text == "ToString" && m.Count == 0 && m.Has(Mods.Override) && !m.Has(Mods.Sealed);

    private static string InheritanceModifierName(Node m)
    {
        if (m.Has(Mods.Override)) return "OverrideMember";
        if (m.Has(Mods.Virtual)) return "VirtualMember";
        if (m.Has(Mods.Abstract)) return "AbstractMember";
        if (m.Has(Mods.Sealed)) return "SealedMember";
        if (m.Has(Mods.Protected)) return "ProtectedMember";
        return "";
    }

    // 演算子の overload は 2 引数の + - * / % と 1 引数の - だけ
    private static bool IsSupportedOperator(string op, int arity)
    {
        if (arity == 2) return op == "+" || op == "-" || op == "*" || op == "/" || op == "%";
        return arity == 1 && op == "-";
    }

    private void Attributes(Node? holder)
    {
        if (holder == null || holder.Kind != NodeKind.Attribute || holder.List == null) return;
        foreach (var a in holder.List)
        {
            string name = SimpleAttributeName(a.Text);
            if (name == "Conditional") Report("ConditionalAttribute", a);
        }
    }

    // System.Diagnostics.ConditionalAttribute → Conditional
    private static string SimpleAttributeName(string text)
    {
        var parts = text.Split('.');
        string last = parts[parts.Length - 1];
        if (last.EndsWith("Attribute") && last != "Attribute")
        {
            var b = new Utf8Bytes(last);
            last = b.Slice(0, b.Length - 9).ToString();
        }
        return last;
    }

    private void Params(Node m, bool outAllowed)
    {
        if (m.List == null) return;
        foreach (var p in m.List)
        {
            if (p.D != null && p.D.List != null)
            {
                foreach (var a in p.D.List)
                {
                    string name = SimpleAttributeName(a.Text);
                    if (CallerInfoNames.Contains(name)) Report("CallerInfoAttribute(" + name + ")", a);
                }
            }
            if (p.Has(Mods.Out) && !outAllowed) Report("OutParameter", p);
            LocalName(p.Text, p);
            if (p.B != null) Walk(p.B);
        }
    }

    private void Body(Node m)
    {
        if (m.A != null && m.A.Kind != NodeKind.TypeName && m.A.Kind != NodeKind.PredefType && m.A.Kind != NodeKind.ArrayType
            && m.A.Kind != NodeKind.NullableType && m.A.Kind != NodeKind.TupleType) Walk(m.A);
        if (m.B != null) Walk(m.B);
        if (m.C != null) Walk(m.C);
    }

    // ---- 名前 ----

    // 全ての宣言に共通の規則 (`__` で始まる名前)。非 ASCII の名前は lexer が報告する
    private static string IdentifierRule(string name)
    {
        if (name.StartsWith("__")) return "DoubleUnderscoreIdentifier(" + name + ")";
        return "";
    }

    private static bool IsUnsafeLuaIdentifier(string name) => LuaKeywords.Contains(name) || name == "self";

    private static string UnsafeLuaIdentifierName(string name) =>
        LuaKeywords.Contains(name) ? "LuaKeywordIdentifier(" + name + ")" : "ReservedIdentifier(" + name + ")";

    // field、property、method、enum の member、record の positional parameter
    private void MemberName(string name, Node at)
    {
        string common = IdentifierRule(name);
        if (common != "") Report(common, at);
        else if (ReservedClassKeys.Contains(name)) Report("ReservedClassKey(" + name + ")", at);
        else if (IsUnsafeLuaIdentifier(name)) Report(UnsafeLuaIdentifierName(name), at);
    }

    // local、parameter、foreach の変数、pattern の変数 (予約語と同じ名前は LocalNames が改名する)
    private void LocalName(string name, Node at)
    {
        if (name == "") return;
        string common = IdentifierRule(name);
        if (common != "") Report(common, at);
        else if (name == "self") Report("ReservedIdentifier(self)", at);
    }

    private void Initializer(Node init)
    {
        // `int[] a = { 1, 2 }` (new の無い配列の初期化子)
        if (init.Kind == NodeKind.ArrayInit) Report("expression: ArrayInitializerExpression", init);
        Walk(init);
    }

    // ---- 文と式 ----

    private void Walk(Node? n)
    {
        if (n == null) return;
        switch (n.Kind)
        {
            case NodeKind.LocalDecl:
                if (n.List != null)
                {
                    foreach (var v in n.List)
                    {
                        LocalName(v.Text, v);
                        if (v.A != null) Initializer(v.A);
                    }
                }
                return;
            case NodeKind.Foreach:
                LocalName(n.Text, n);
                break;
            case NodeKind.PatDecl:
                LocalName(n.Text, n);
                break;
            case NodeKind.Lambda:
                if (n.List != null)
                {
                    foreach (var p in n.List)
                    {
                        if (p.Has(Mods.Out)) Report("OutParameter", p);
                        LocalName(p.Text, p);
                    }
                }
                Walk(n.A);
                Walk(n.B);
                return;
            case NodeKind.TypeDecl:
                return;
            case NodeKind.Tuple:
                if (n.Count > 7) Report("TupleArity", n);
                break;
        }
        Walk(n.A);
        Walk(n.B);
        Walk(n.C);
        Walk(n.D);
        if (n.List != null)
        {
            foreach (var x in n.List) Walk(x);
        }
        if (n.List2 != null)
        {
            foreach (var x in n.List2) Walk(x);
        }
        if (n.List3 != null)
        {
            foreach (var x in n.List3) Walk(x);
        }
    }
}
