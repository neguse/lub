// SelfHost/VmBackend/gen-ilnodes.py が SelfHost/Compiler/Il.cs から作る。手で編集しない
using System.Collections.Generic;
using TcsSelf;

namespace TcsVm;

/// <summary>IL の node の子 (Children) と診断用のテキスト (Dump)。member は record の宣言の順 (Origin を除く)。
/// Dump は lambda の本文の指紋 (reload が生きている closure を新版に対応させる鍵) に使う。</summary>
public static class IlNodes
{
    /// <summary>node の子 (IL の式と文、IlBlock、IlTableEntry)。if の腕は条件と本文、with の上書きは値。</summary>
    public static List<object> Children(object node)
    {
        var r = new List<object>();
        if (node is IlLit nIlLit)
        {
            var n = nIlLit;
        }
        else if (node is IlVar nIlVar)
        {
            var n = nIlVar;
        }
        else if (node is IlField nIlField)
        {
            var n = nIlField;
            if (n.Recv != null) r.Add(n.Recv);
        }
        else if (node is IlIndex nIlIndex)
        {
            var n = nIlIndex;
            if (n.Recv != null) r.Add(n.Recv);
            if (n.Idx != null) r.Add(n.Idx);
        }
        else if (node is IlLen nIlLen)
        {
            var n = nIlLen;
            if (n.E != null) r.Add(n.E);
        }
        else if (node is IlBin nIlBin)
        {
            var n = nIlBin;
            if (n.L != null) r.Add(n.L);
            if (n.R != null) r.Add(n.R);
        }
        else if (node is IlUn nIlUn)
        {
            var n = nIlUn;
            if (n.E != null) r.Add(n.E);
        }
        else if (node is IlParen nIlParen)
        {
            var n = nIlParen;
            if (n.E != null) r.Add(n.E);
        }
        else if (node is IlValues nIlValues)
        {
            var n = nIlValues;
            foreach (var x in n.Items) r.Add(x);
        }
        else if (node is IlTernary nIlTernary)
        {
            var n = nIlTernary;
            if (n.Cond != null) r.Add(n.Cond);
            if (n.T != null) r.Add(n.T);
            if (n.F != null) r.Add(n.F);
        }
        else if (node is IlCall nIlCall)
        {
            var n = nIlCall;
            foreach (var x in n.Args) r.Add(x);
        }
        else if (node is IlDynCall nIlDynCall)
        {
            var n = nIlDynCall;
            if (n.Callee != null) r.Add(n.Callee);
            foreach (var x in n.Args) r.Add(x);
        }
        else if (node is IlInvoke nIlInvoke)
        {
            var n = nIlInvoke;
            if (n.Recv != null) r.Add(n.Recv);
            foreach (var x in n.Args) r.Add(x);
        }
        else if (node is IlNewObj nIlNewObj)
        {
            var n = nIlNewObj;
            foreach (var x in n.Args) r.Add(x);
        }
        else if (node is IlTableEntry nIlTableEntry)
        {
            var n = nIlTableEntry;
            if (n.Key != null) r.Add(n.Key);
            if (n.Value != null) r.Add(n.Value);
        }
        else if (node is IlTable nIlTable)
        {
            var n = nIlTable;
            foreach (var x in n.Entries) r.Add(x);
        }
        else if (node is IlNewArray nIlNewArray)
        {
            var n = nIlNewArray;
            if (n.Length != null) r.Add(n.Length);
            if (n.Default != null) r.Add(n.Default);
        }
        else if (node is IlNewQueue nIlNewQueue)
        {
            var n = nIlNewQueue;
        }
        else if (node is IlNewHashSet nIlNewHashSet)
        {
            var n = nIlNewHashSet;
        }
        else if (node is IlNumericConvert nIlNumericConvert)
        {
            var n = nIlNumericConvert;
            if (n.Value != null) r.Add(n.Value);
        }
        else if (node is IlRefCast nIlRefCast)
        {
            var n = nIlRefCast;
            if (n.Value != null) r.Add(n.Value);
            if (n.LuaCheckArg != null) r.Add(n.LuaCheckArg);
        }
        else if (node is IlIsType nIlIsType)
        {
            var n = nIlIsType;
            if (n.E != null) r.Add(n.E);
        }
        else if (node is IlIsLuaType nIlIsLuaType)
        {
            var n = nIlIsLuaType;
            if (n.E != null) r.Add(n.E);
        }
        else if (node is IlIife nIlIife)
        {
            var n = nIlIife;
            foreach (var x in n.Stats) r.Add(x);
        }
        else if (node is IlClosure nIlClosure)
        {
            var n = nIlClosure;
            if (n.Body != null) r.Add(n.Body);
            if (n.ExprBody != null) r.Add(n.ExprBody);
        }
        else if (node is IlMethodGroup nIlMethodGroup)
        {
            var n = nIlMethodGroup;
        }
        else if (node is IlStructCopy nIlStructCopy)
        {
            var n = nIlStructCopy;
            if (n.E != null) r.Add(n.E);
        }
        else if (node is IlNullableWrap nIlNullableWrap)
        {
            var n = nIlNullableWrap;
            if (n.E != null) r.Add(n.E);
        }
        else if (node is IlNullableHasValue nIlNullableHasValue)
        {
            var n = nIlNullableHasValue;
            if (n.E != null) r.Add(n.E);
        }
        else if (node is IlNullableValue nIlNullableValue)
        {
            var n = nIlNullableValue;
            if (n.E != null) r.Add(n.E);
        }
        else if (node is IlNullableGetOrDefault nIlNullableGetOrDefault)
        {
            var n = nIlNullableGetOrDefault;
            if (n.E != null) r.Add(n.E);
            if (n.Default != null) r.Add(n.Default);
        }
        else if (node is IlLiftedBin nIlLiftedBin)
        {
            var n = nIlLiftedBin;
            if (n.L != null) r.Add(n.L);
            if (n.R != null) r.Add(n.R);
        }
        else if (node is IlLiftedUn nIlLiftedUn)
        {
            var n = nIlLiftedUn;
            if (n.E != null) r.Add(n.E);
        }
        else if (node is IlCast nIlCast)
        {
            var n = nIlCast;
            if (n.E != null) r.Add(n.E);
        }
        else if (node is IlStructDescriptor nIlStructDescriptor)
        {
            var n = nIlStructDescriptor;
            foreach (var x in n.NewArgs) r.Add(x);
        }
        else if (node is IlWith nIlWith)
        {
            var n = nIlWith;
            if (n.Src != null) r.Add(n.Src);
            foreach (var x in n.Overrides) r.Add(x.Value);
        }
        else if (node is IlBlock nIlBlock)
        {
            var n = nIlBlock;
            foreach (var x in n.Stats) r.Add(x);
        }
        else if (node is IlLocal nIlLocal)
        {
            var n = nIlLocal;
            if (n.Init != null) r.Add(n.Init);
        }
        else if (node is IlAssign nIlAssign)
        {
            var n = nIlAssign;
            if (n.Target != null) r.Add(n.Target);
            if (n.Value != null) r.Add(n.Value);
        }
        else if (node is IlCallStat nIlCallStat)
        {
            var n = nIlCallStat;
            if (n.Call != null) r.Add(n.Call);
        }
        else if (node is IlIf nIlIf)
        {
            var n = nIlIf;
            foreach (var x in n.Arms)
            {
                r.Add(x.Cond);
                r.Add(x.Body);
            }
            if (n.Else != null) r.Add(n.Else);
        }
        else if (node is IlWhile nIlWhile)
        {
            var n = nIlWhile;
            if (n.Cond != null) r.Add(n.Cond);
            if (n.Body != null) r.Add(n.Body);
            if (n.Trailer != null) r.Add(n.Trailer);
        }
        else if (node is IlRepeat nIlRepeat)
        {
            var n = nIlRepeat;
            if (n.Body != null) r.Add(n.Body);
            if (n.Cond != null) r.Add(n.Cond);
        }
        else if (node is IlNumericFor nIlNumericFor)
        {
            var n = nIlNumericFor;
            if (n.Start != null) r.Add(n.Start);
            if (n.Limit != null) r.Add(n.Limit);
            if (n.Body != null) r.Add(n.Body);
        }
        else if (node is IlForeachList nIlForeachList)
        {
            var n = nIlForeachList;
            if (n.Coll != null) r.Add(n.Coll);
            if (n.Body != null) r.Add(n.Body);
        }
        else if (node is IlForeachDict nIlForeachDict)
        {
            var n = nIlForeachDict;
            if (n.Coll != null) r.Add(n.Coll);
            if (n.Body != null) r.Add(n.Body);
        }
        else if (node is IlForeachRunes nIlForeachRunes)
        {
            var n = nIlForeachRunes;
            if (n.Str != null) r.Add(n.Str);
            if (n.Body != null) r.Add(n.Body);
        }
        else if (node is IlBreak nIlBreak)
        {
            var n = nIlBreak;
        }
        else if (node is IlContinue nIlContinue)
        {
            var n = nIlContinue;
        }
        else if (node is IlReturn nIlReturn)
        {
            var n = nIlReturn;
            if (n.Value != null) r.Add(n.Value);
        }
        else if (node is IlDo nIlDo)
        {
            var n = nIlDo;
            if (n.Body != null) r.Add(n.Body);
        }
        else if (node is IlBreakScope nIlBreakScope)
        {
            var n = nIlBreakScope;
            if (n.Body != null) r.Add(n.Body);
        }
        else if (node is IlMultiAssign nIlMultiAssign)
        {
            var n = nIlMultiAssign;
            foreach (var x in n.Targets) r.Add(x);
            foreach (var x in n.Values) r.Add(x);
        }
        else if (node is IlForPairs nIlForPairs)
        {
            var n = nIlForPairs;
            if (n.Coll != null) r.Add(n.Coll);
            if (n.Body != null) r.Add(n.Body);
        }
        return r;
    }

    /// <summary>node の record の名前 (C# の GetType().Name)。</summary>
    public static string Name(object node)
    {
        if (node is IlLit) return "IlLit";
        if (node is IlVar) return "IlVar";
        if (node is IlField) return "IlField";
        if (node is IlIndex) return "IlIndex";
        if (node is IlLen) return "IlLen";
        if (node is IlBin) return "IlBin";
        if (node is IlUn) return "IlUn";
        if (node is IlParen) return "IlParen";
        if (node is IlValues) return "IlValues";
        if (node is IlTernary) return "IlTernary";
        if (node is IlCall) return "IlCall";
        if (node is IlDynCall) return "IlDynCall";
        if (node is IlInvoke) return "IlInvoke";
        if (node is IlNewObj) return "IlNewObj";
        if (node is IlTableEntry) return "IlTableEntry";
        if (node is IlTable) return "IlTable";
        if (node is IlNewArray) return "IlNewArray";
        if (node is IlNewQueue) return "IlNewQueue";
        if (node is IlNewHashSet) return "IlNewHashSet";
        if (node is IlNumericConvert) return "IlNumericConvert";
        if (node is IlRefCast) return "IlRefCast";
        if (node is IlIsType) return "IlIsType";
        if (node is IlIsLuaType) return "IlIsLuaType";
        if (node is IlIife) return "IlIife";
        if (node is IlClosure) return "IlClosure";
        if (node is IlMethodGroup) return "IlMethodGroup";
        if (node is IlStructCopy) return "IlStructCopy";
        if (node is IlNullableWrap) return "IlNullableWrap";
        if (node is IlNullableHasValue) return "IlNullableHasValue";
        if (node is IlNullableValue) return "IlNullableValue";
        if (node is IlNullableGetOrDefault) return "IlNullableGetOrDefault";
        if (node is IlLiftedBin) return "IlLiftedBin";
        if (node is IlLiftedUn) return "IlLiftedUn";
        if (node is IlCast) return "IlCast";
        if (node is IlStructDescriptor) return "IlStructDescriptor";
        if (node is IlOverride) return "IlOverride";
        if (node is IlWith) return "IlWith";
        if (node is IlBlock) return "IlBlock";
        if (node is IlLocal) return "IlLocal";
        if (node is IlAssign) return "IlAssign";
        if (node is IlCallStat) return "IlCallStat";
        if (node is IlArm) return "IlArm";
        if (node is IlIf) return "IlIf";
        if (node is IlWhile) return "IlWhile";
        if (node is IlRepeat) return "IlRepeat";
        if (node is IlNumericFor) return "IlNumericFor";
        if (node is IlForeachList) return "IlForeachList";
        if (node is IlForeachDict) return "IlForeachDict";
        if (node is IlForeachRunes) return "IlForeachRunes";
        if (node is IlBreak) return "IlBreak";
        if (node is IlContinue) return "IlContinue";
        if (node is IlReturn) return "IlReturn";
        if (node is IlDo) return "IlDo";
        if (node is IlBreakScope) return "IlBreakScope";
        if (node is IlMultiAssign) return "IlMultiAssign";
        if (node is IlForPairs) return "IlForPairs";
        return "?";
    }

    public static string Dump(object? node)
    {
        if (node == null) return "null";
        if (node is IlLit nIlLit)
        {
            var n = nIlLit;
            return "IlLit(" + IlLitKindName(n.Kind) + ", " + IlLiterals.Display(n) + ")";
        }
        if (node is IlVar nIlVar)
        {
            var n = nIlVar;
            return "IlVar(" + Str(n.Name) + ")";
        }
        if (node is IlField nIlField)
        {
            var n = nIlField;
            return "IlField(" + Dump(n.Recv) + ", " + Str(n.Name) + ")";
        }
        if (node is IlIndex nIlIndex)
        {
            var n = nIlIndex;
            return "IlIndex(" + Dump(n.Recv) + ", " + Dump(n.Idx) + ", " + (n.PlusOne ? "True" : "False") + ", " + (n.Unchecked ? "True" : "False") + ", " + (n.BoolElements ? "True" : "False") + ", " + (n.NilElements ? "True" : "False") + ")";
        }
        if (node is IlLen nIlLen)
        {
            var n = nIlLen;
            return "IlLen(" + Dump(n.E) + ", " + (n.NilElements ? "True" : "False") + ")";
        }
        if (node is IlBin nIlBin)
        {
            var n = nIlBin;
            return "IlBin(" + IlBinOpName(n.Op) + ", " + Dump(n.L) + ", " + Dump(n.R) + ")";
        }
        if (node is IlUn nIlUn)
        {
            var n = nIlUn;
            return "IlUn(" + IlUnOpName(n.Op) + ", " + Dump(n.E) + ")";
        }
        if (node is IlParen nIlParen)
        {
            var n = nIlParen;
            return "IlParen(" + Dump(n.E) + ")";
        }
        if (node is IlValues nIlValues)
        {
            var n = nIlValues;
            return "IlValues(" + Nodes(n.Items) + ")";
        }
        if (node is IlTernary nIlTernary)
        {
            var n = nIlTernary;
            return "IlTernary(" + Dump(n.Cond) + ", " + Dump(n.T) + ", " + Dump(n.F) + ")";
        }
        if (node is IlCall nIlCall)
        {
            var n = nIlCall;
            return "IlCall(" + Str(n.Callee) + ", " + Nodes(n.Args) + ", " + (n.NilElements ? "True" : "False") + ")";
        }
        if (node is IlDynCall nIlDynCall)
        {
            var n = nIlDynCall;
            return "IlDynCall(" + Dump(n.Callee) + ", " + Nodes(n.Args) + ")";
        }
        if (node is IlInvoke nIlInvoke)
        {
            var n = nIlInvoke;
            return "IlInvoke(" + Dump(n.Recv) + ", " + Str(n.Method) + ", " + Nodes(n.Args) + ")";
        }
        if (node is IlNewObj nIlNewObj)
        {
            var n = nIlNewObj;
            return "IlNewObj(" + Str(n.TypeName) + ", " + Nodes(n.Args) + ", " + Str(n.Ctor) + ")";
        }
        if (node is IlTableEntry nIlTableEntry)
        {
            var n = nIlTableEntry;
            return "IlTableEntry(" + Dump(n.Key) + ", " + Dump(n.Value) + ", " + Str(n.NameKey) + ")";
        }
        if (node is IlTable nIlTable)
        {
            var n = nIlTable;
            return "IlTable(" + Nodes(n.Entries) + ", " + Str(n.ElementType) + ", " + Str(n.KeyType) + ", " + (n.IsArray ? "True" : "False") + ", " + Str(n.ObjectType) + ", " + (n.NilElements ? "True" : "False") + ")";
        }
        if (node is IlNewArray nIlNewArray)
        {
            var n = nIlNewArray;
            return "IlNewArray(" + Str(n.ElementType) + ", " + Dump(n.Length) + ", " + Dump(n.Default) + ")";
        }
        if (node is IlNewQueue nIlNewQueue)
        {
            var n = nIlNewQueue;
            return "IlNewQueue(" + Str(n.Type) + ")";
        }
        if (node is IlNewHashSet nIlNewHashSet)
        {
            var n = nIlNewHashSet;
            return "IlNewHashSet(" + Str(n.Type) + ")";
        }
        if (node is IlNumericConvert nIlNumericConvert)
        {
            var n = nIlNumericConvert;
            return "IlNumericConvert(" + Dump(n.Value) + ", " + Str(n.TargetType) + ", " + Str(n.LuaCheck) + ")";
        }
        if (node is IlRefCast nIlRefCast)
        {
            var n = nIlRefCast;
            return "IlRefCast(" + Dump(n.Value) + ", " + Str(n.TargetType) + ", " + Str(n.LuaCheck) + ", " + Dump(n.LuaCheckArg) + ")";
        }
        if (node is IlIsType nIlIsType)
        {
            var n = nIlIsType;
            return "IlIsType(" + Dump(n.E) + ", " + Str(n.TypeRef) + ", " + (n.IsInterface ? "True" : "False") + ")";
        }
        if (node is IlIsLuaType nIlIsLuaType)
        {
            var n = nIlIsLuaType;
            return "IlIsLuaType(" + Dump(n.E) + ", " + Str(n.LuaType) + ")";
        }
        if (node is IlIife nIlIife)
        {
            var n = nIlIife;
            return "IlIife(" + Nodes(n.Stats) + ")";
        }
        if (node is IlClosure nIlClosure)
        {
            var n = nIlClosure;
            return "IlClosure(" + Strs(n.Params) + ", " + Dump(n.Body) + ", " + Dump(n.ExprBody) + ", " + Strs(n.PatternLocals) + ")";
        }
        if (node is IlMethodGroup nIlMethodGroup)
        {
            var n = nIlMethodGroup;
            return "IlMethodGroup(" + Str(n.TypeRef) + ", " + Str(n.Method) + ", " + NullableStrs(n.CopyTypes) + ")";
        }
        if (node is IlStructCopy nIlStructCopy)
        {
            var n = nIlStructCopy;
            return "IlStructCopy(" + Dump(n.E) + ", " + Str(n.TypeName) + ", " + (n.Nullable ? "True" : "False") + ", " + Str(n.Copiers) + ")";
        }
        if (node is IlNullableWrap nIlNullableWrap)
        {
            var n = nIlNullableWrap;
            return "IlNullableWrap(" + Dump(n.E) + ", " + Str(n.Type) + ")";
        }
        if (node is IlNullableHasValue nIlNullableHasValue)
        {
            var n = nIlNullableHasValue;
            return "IlNullableHasValue(" + Dump(n.E) + ")";
        }
        if (node is IlNullableValue nIlNullableValue)
        {
            var n = nIlNullableValue;
            return "IlNullableValue(" + Dump(n.E) + ")";
        }
        if (node is IlNullableGetOrDefault nIlNullableGetOrDefault)
        {
            var n = nIlNullableGetOrDefault;
            return "IlNullableGetOrDefault(" + Dump(n.E) + ", " + Dump(n.Default) + ")";
        }
        if (node is IlLiftedBin nIlLiftedBin)
        {
            var n = nIlLiftedBin;
            return "IlLiftedBin(" + IlLiftedOpName(n.Op) + ", " + Dump(n.L) + ", " + Dump(n.R) + ")";
        }
        if (node is IlLiftedUn nIlLiftedUn)
        {
            var n = nIlLiftedUn;
            return "IlLiftedUn(" + IlUnOpName(n.Op) + ", " + Dump(n.E) + ")";
        }
        if (node is IlCast nIlCast)
        {
            var n = nIlCast;
            return "IlCast(" + Dump(n.E) + ", " + Str(n.TypeRef) + ", " + Str(n.LuaRef) + ")";
        }
        if (node is IlStructDescriptor nIlStructDescriptor)
        {
            var n = nIlStructDescriptor;
            return "IlStructDescriptor(" + Str(n.TypeName) + ", " + Nodes(n.NewArgs) + ", " + Str(n.Copiers) + ", " + (n.HasToString ? "True" : "False") + ")";
        }
        if (node is IlWith nIlWith)
        {
            var n = nIlWith;
            return "IlWith(" + Dump(n.Src) + ", " + Overrides(n.Overrides) + ")";
        }
        if (node is IlBlock nIlBlock)
        {
            var n = nIlBlock;
            var stats = new List<string>();
            foreach (var s in n.Stats) stats.Add(Dump(s));
            return "{ " + string.Join("; ", stats) + " }";
        }
        if (node is IlLocal nIlLocal)
        {
            var n = nIlLocal;
            return "IlLocal(" + Str(n.Name) + ", " + Dump(n.Init) + ", " + Str(n.Type) + ")";
        }
        if (node is IlAssign nIlAssign)
        {
            var n = nIlAssign;
            return "IlAssign(" + Dump(n.Target) + ", " + Dump(n.Value) + ")";
        }
        if (node is IlCallStat nIlCallStat)
        {
            var n = nIlCallStat;
            return "IlCallStat(" + Dump(n.Call) + ")";
        }
        if (node is IlIf nIlIf)
        {
            var n = nIlIf;
            return "IlIf(" + Arms(n.Arms) + ", " + Dump(n.Else) + ")";
        }
        if (node is IlWhile nIlWhile)
        {
            var n = nIlWhile;
            return "IlWhile(" + Dump(n.Cond) + ", " + Dump(n.Body) + ", " + Dump(n.Trailer) + ", " + (n.ScopeBody ? "True" : "False") + ")";
        }
        if (node is IlRepeat nIlRepeat)
        {
            var n = nIlRepeat;
            return "IlRepeat(" + Dump(n.Body) + ", " + Dump(n.Cond) + ")";
        }
        if (node is IlNumericFor nIlNumericFor)
        {
            var n = nIlNumericFor;
            return "IlNumericFor(" + Str(n.Var) + ", " + Dump(n.Start) + ", " + Dump(n.Limit) + ", " + Dump(n.Body) + ")";
        }
        if (node is IlForeachList nIlForeachList)
        {
            var n = nIlForeachList;
            return "IlForeachList(" + Str(n.Var) + ", " + Dump(n.Coll) + ", " + Dump(n.Body) + ", " + (n.Counted ? "True" : "False") + ")";
        }
        if (node is IlForeachDict nIlForeachDict)
        {
            var n = nIlForeachDict;
            return "IlForeachDict(" + Str(n.Var) + ", " + Dump(n.Coll) + ", " + Dump(n.Body) + ")";
        }
        if (node is IlForeachRunes nIlForeachRunes)
        {
            var n = nIlForeachRunes;
            return "IlForeachRunes(" + Str(n.Var) + ", " + Dump(n.Str) + ", " + Dump(n.Body) + ")";
        }
        if (node is IlBreak nIlBreak)
        {
            var n = nIlBreak;
            return "IlBreak(" + "" + ")";
        }
        if (node is IlContinue nIlContinue)
        {
            var n = nIlContinue;
            return "IlContinue(" + "" + ")";
        }
        if (node is IlReturn nIlReturn)
        {
            var n = nIlReturn;
            return "IlReturn(" + Dump(n.Value) + ")";
        }
        if (node is IlDo nIlDo)
        {
            var n = nIlDo;
            return "IlDo(" + Dump(n.Body) + ")";
        }
        if (node is IlBreakScope nIlBreakScope)
        {
            var n = nIlBreakScope;
            return "IlBreakScope(" + Dump(n.Body) + ")";
        }
        if (node is IlMultiAssign nIlMultiAssign)
        {
            var n = nIlMultiAssign;
            return "IlMultiAssign(" + Nodes(n.Targets) + ", " + Nodes(n.Values) + ", " + (n.Declare ? "True" : "False") + ")";
        }
        if (node is IlForPairs nIlForPairs)
        {
            var n = nIlForPairs;
            return "IlForPairs(" + Str(n.KVar) + ", " + Str(n.VVar) + ", " + Dump(n.Coll) + ", " + Dump(n.Body) + ")";
        }
        return "?";
    }

    private static string Str(string? s) => s == null ? "null" : "\"" + s + "\"";

    private static string Strs(List<string> items)
    {
        var parts = new List<string>();
        foreach (var s in items) parts.Add(Str(s));
        return "[" + string.Join(", ", parts) + "]";
    }

    private static string NullableStrs(List<string?> items)
    {
        var parts = new List<string>();
        foreach (var s in items) parts.Add(Str(s));
        return "[" + string.Join(", ", parts) + "]";
    }

    private static string Nodes(List<IlExpr> items)
    {
        var parts = new List<string>();
        foreach (var x in items) parts.Add(Dump(x));
        return "[" + string.Join(", ", parts) + "]";
    }

    private static string Nodes(List<IlStat> items)
    {
        var parts = new List<string>();
        foreach (var x in items) parts.Add(Dump(x));
        return "[" + string.Join(", ", parts) + "]";
    }

    private static string Nodes(List<IlTableEntry> items)
    {
        var parts = new List<string>();
        foreach (var x in items) parts.Add(Dump(x));
        return "[" + string.Join(", ", parts) + "]";
    }

    // if の腕と with の上書きは C# 版では tuple
    private static string Arms(List<IlArm> items)
    {
        var parts = new List<string>();
        foreach (var x in items) parts.Add("(" + Dump(x.Cond) + ", " + Dump(x.Body) + ")");
        return "[" + string.Join(", ", parts) + "]";
    }

    private static string Overrides(List<IlOverride> items)
    {
        var parts = new List<string>();
        foreach (var x in items) parts.Add("(" + Str(x.Name) + ", " + Dump(x.Value) + ")");
        return "[" + string.Join(", ", parts) + "]";
    }

    public static string IlLitKindName(IlLitKind v)
    {
        if (v == IlLitKind.Null) return "Null";
        if (v == IlLitKind.Bool) return "Bool";
        if (v == IlLitKind.Int) return "Int";
        if (v == IlLitKind.Float) return "Float";
        if (v == IlLitKind.String) return "String";
        return "" + (int)v;
    }

    public static string IlBinOpName(IlBinOp v)
    {
        if (v == IlBinOp.AddNum) return "AddNum";
        if (v == IlBinOp.Concat) return "Concat";
        if (v == IlBinOp.Sub) return "Sub";
        if (v == IlBinOp.Mul) return "Mul";
        if (v == IlBinOp.DivNum) return "DivNum";
        if (v == IlBinOp.RemNum) return "RemNum";
        if (v == IlBinOp.Eq) return "Eq";
        if (v == IlBinOp.Ne) return "Ne";
        if (v == IlBinOp.Lt) return "Lt";
        if (v == IlBinOp.Le) return "Le";
        if (v == IlBinOp.Gt) return "Gt";
        if (v == IlBinOp.Ge) return "Ge";
        if (v == IlBinOp.And) return "And";
        if (v == IlBinOp.Or) return "Or";
        if (v == IlBinOp.BitAnd) return "BitAnd";
        if (v == IlBinOp.BitOr) return "BitOr";
        if (v == IlBinOp.BitXor) return "BitXor";
        if (v == IlBinOp.Shl) return "Shl";
        if (v == IlBinOp.Shr) return "Shr";
        return "" + (int)v;
    }

    public static string IlUnOpName(IlUnOp v)
    {
        if (v == IlUnOp.Neg) return "Neg";
        if (v == IlUnOp.Not) return "Not";
        if (v == IlUnOp.BitNot) return "BitNot";
        if (v == IlUnOp.ToFloat) return "ToFloat";
        return "" + (int)v;
    }

    public static string IlLiftedOpName(IlLiftedOp v)
    {
        if (v == IlLiftedOp.Add) return "Add";
        if (v == IlLiftedOp.Sub) return "Sub";
        if (v == IlLiftedOp.Mul) return "Mul";
        if (v == IlLiftedOp.DivFloat) return "DivFloat";
        if (v == IlLiftedOp.DivInt) return "DivInt";
        if (v == IlLiftedOp.RemFloat) return "RemFloat";
        if (v == IlLiftedOp.RemInt) return "RemInt";
        if (v == IlLiftedOp.BitAnd) return "BitAnd";
        if (v == IlLiftedOp.BitOr) return "BitOr";
        if (v == IlLiftedOp.BitXor) return "BitXor";
        if (v == IlLiftedOp.Shl) return "Shl";
        if (v == IlLiftedOp.Shr) return "Shr";
        if (v == IlLiftedOp.Eq) return "Eq";
        if (v == IlLiftedOp.Ne) return "Ne";
        if (v == IlLiftedOp.Lt) return "Lt";
        if (v == IlLiftedOp.Le) return "Le";
        if (v == IlLiftedOp.Gt) return "Gt";
        if (v == IlLiftedOp.Ge) return "Ge";
        if (v == IlLiftedOp.And) return "And";
        if (v == IlLiftedOp.Or) return "Or";
        return "" + (int)v;
    }
}
