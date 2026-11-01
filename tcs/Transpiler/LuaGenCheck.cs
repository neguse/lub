using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace TinyCs;

// 単一の実装へ移す途中の検査 (TCS_LUAGEN_CHECK=1)。IL を Lua にする入口で、同じ IL を
// サブセットに写した emitter (SelfHost/Compiler/LuaGen.cs) にも渡し、出力と状態
// (Lua の行、source map、continue の label、lowering の temp) が一致することを確かめる。
// 出力は参照実装のものを使う。食い違いは例外で止める
public partial class LuaEmitter
{
    internal static readonly bool LuaGenCheck = InitLuaGenCheck();

    internal static int LuaGenChecks;

    private static bool InitLuaGenCheck()
    {
        if (Environment.GetEnvironmentVariable("TCS_LUAGEN_CHECK") != "1") return false;
        if (Environment.GetEnvironmentVariable("TCS_LUAGEN_CHECK_COUNT") == "1")
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Console.Error.WriteLine($"luagen-check: {LuaGenChecks} compared");
        return true;
    }

    private readonly Dictionary<SyntaxNode, TcsSelf.IlPos> _portOrigins = new();

    private void CheckedEmitIlBlock(IlBlock block)
    {
        if (!LuaGenCheck)
        {
            EmitIlBlock(block);
            return;
        }
        var before = SnapshotForPort();
        EmitIlBlock(block);
        var port = NewPort(before);
        port.EmitIlBlock((TcsSelf.IlBlock)ToPort(block)!, null);
        ComparePort(before, port, _sb.ToString(before.SbLength, _sb.Length - before.SbLength), "EmitIlBlock");
    }

    private void CheckedEmitIlStat(IlStat stat)
    {
        if (!LuaGenCheck)
        {
            EmitIlStat(stat);
            return;
        }
        var before = SnapshotForPort();
        EmitIlStat(stat);
        var port = NewPort(before);
        port.EmitIlStat((TcsSelf.IlStat)ToPort(stat)!, true);
        ComparePort(before, port, _sb.ToString(before.SbLength, _sb.Length - before.SbLength), "EmitIlStat");
    }

    private string CheckedRenderIl(IlExpr expr)
    {
        if (!LuaGenCheck) return RenderIl(expr);
        var before = SnapshotForPort();
        var text = RenderIl(expr);
        var port = NewPort(before);
        var portText = port.RenderIl((TcsSelf.IlExpr)ToPort(expr)!);
        if (portText != text)
            throw new InvalidOperationException($"LuaGen RenderIl differs:\n--- reference\n{text}\n--- port\n{portText}");
        ComparePort(before, port, "", "RenderIl");
        return text;
    }

    private string CheckedRenderStructDescriptor(IlStructDescriptor descriptor, bool inline)
    {
        if (!LuaGenCheck) return RenderStructDescriptor(descriptor, inline);
        var text = RenderStructDescriptor(descriptor, inline);
        var port = NewPort(SnapshotForPort());
        var portText = port.RenderStructDescriptor((TcsSelf.IlStructDescriptor)ToPort(descriptor)!, inline);
        if (portText != text)
            throw new InvalidOperationException($"LuaGen RenderStructDescriptor differs:\n--- reference\n{text}\n--- port\n{portText}");
        return text;
    }

    private sealed record PortSnapshot(int SbLength, int Indent, int LuaLine,
        (string File, int Line)? Source, int ContinueCounter, int[] ContinueStack,
        int[] UsedLabels, bool Trace, int LowerTemp);

    private PortSnapshot SnapshotForPort() => new(_sb.Length, _indent, _luaLine, _currentSource,
        _continueCounter, [.. _continueStack.Reverse()], [.. _usedContinueLabels],
        _traceFunction, _lowerTempCount);

    private static TcsSelf.LuaGen NewPort(PortSnapshot s)
    {
        var port = new TcsSelf.LuaGen
        {
            Indent = s.Indent,
            LuaLine = s.LuaLine,
            ContinueCounter = s.ContinueCounter,
            TraceFunction = s.Trace,
            LowerTempCount = s.LowerTemp,
        };
        if (s.Source is { } src)
        {
            port.SourceFile = src.File;
            port.SourceLine = src.Line;
        }
        foreach (var l in s.ContinueStack) port.ContinueStack.Add(l);
        foreach (var l in s.UsedLabels) port.UsedContinueLabels.Add(l);
        return port;
    }

    private void ComparePort(PortSnapshot before, TcsSelf.LuaGen port, string text, string what)
    {
        Interlocked.Increment(ref LuaGenChecks);
        var problems = new List<string>();
        if (port.InternalErrors.Count > 0) problems.Add("internal: " + string.Join("; ", port.InternalErrors));
        if (port.Text() != text) problems.Add($"text\n--- reference\n{text}\n--- port\n{port.Text()}");
        if (port.LuaLine != _luaLine) problems.Add($"LuaLine {_luaLine} vs {port.LuaLine}");
        var portSource = port.SourceFile != null ? (port.SourceFile, port.SourceLine) : ((string, int)?)null;
        if (portSource != _currentSource) problems.Add($"source {_currentSource} vs {portSource}");
        if (port.ContinueCounter != _continueCounter) problems.Add($"continue counter {_continueCounter} vs {port.ContinueCounter}");
        if (!port.UsedContinueLabels.SetEquals(_usedContinueLabels)) problems.Add("used continue labels");
        if (port.LowerTempCount != _lowerTempCount) problems.Add($"lower temp {_lowerTempCount} vs {port.LowerTempCount}");
        if (port.Indent != _indent) problems.Add($"indent {_indent} vs {port.Indent}");
        var refMap = SourceMap.EntriesFrom(before.LuaLine);
        var portMap = port.SourceMap.Select(e => (e.LuaLine, e.File, e.Line)).ToList();
        if (!refMap.SequenceEqual(portMap))
            problems.Add("source map\n--- reference\n" + string.Join("\n", refMap) + "\n--- port\n" + string.Join("\n", portMap));
        if (problems.Count > 0)
            throw new InvalidOperationException($"LuaGen {what} differs: " + string.Join("\n", problems));
    }

    // ---- 参照実装の IL → サブセットの IL ----

    private static readonly Assembly PortAssembly = typeof(TcsSelf.LuaGen).Assembly;
    private static readonly Dictionary<Type, (Type Target, ConstructorInfo Ctor, ParameterInfo[] Params)> PortTypes = new();

    private object? ToPort(object? value)
    {
        switch (value)
        {
            case null: return null;
            case string or bool or int: return value;
            case SyntaxNode node: return PortOrigin(node);
        }
        var type = value.GetType();
        if (type.IsEnum)
            return Enum.Parse(PortAssembly.GetType("TcsSelf." + type.Name)!, value.ToString()!);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
        {
            var isDefault = (bool)type.GetProperty("IsDefault")!.GetValue(value)!;
            return ToPortList(type.GetGenericArguments()[0], isDefault ? Array.Empty<object>() : (IEnumerable)value);
        }
        // if の腕 (Cond, Body) と with の上書き (Name, Value)
        if (value is ValueTuple<IlExpr, IlBlock> arm)
            return new TcsSelf.IlArm((TcsSelf.IlExpr)ToPort(arm.Item1)!, (TcsSelf.IlBlock)ToPort(arm.Item2)!);
        if (value is ValueTuple<string, IlExpr> ov)
            return new TcsSelf.IlOverride(ov.Item1, (TcsSelf.IlExpr)ToPort(ov.Item2)!);
        var (target, ctor, ps) = PortType(type);
        var args = new object?[ps.Length];
        for (var i = 0; i < ps.Length; i++)
        {
            var prop = type.GetProperty(ps[i].Name!)
                ?? throw new InvalidOperationException($"LuaGen check: {type.Name} has no {ps[i].Name}");
            args[i] = ToPort(prop.GetValue(value));
        }
        return ctor.Invoke(args);
    }

    private object ToPortList(Type element, IEnumerable items)
    {
        var portElement = element == typeof(string) ? typeof(string)
            : element == typeof(ValueTuple<IlExpr, IlBlock>) ? typeof(TcsSelf.IlArm)
            : element == typeof(ValueTuple<string, IlExpr>) ? typeof(TcsSelf.IlOverride)
            : PortAssembly.GetType("TcsSelf." + element.Name)!;
        var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(portElement))!;
        foreach (var item in items) list.Add(ToPort(item));
        return list;
    }

    private static (Type, ConstructorInfo, ParameterInfo[]) PortType(Type type)
    {
        lock (PortTypes)
        {
            if (PortTypes.TryGetValue(type, out var cached)) return cached;
            var target = PortAssembly.GetType("TcsSelf." + type.Name)
                ?? throw new InvalidOperationException($"LuaGen check: no TcsSelf.{type.Name}");
            // record の primary constructor (copy constructor 以外で引数が最も多いもの)
            var ctor = target.GetConstructors()
                .Where(c => !(c.GetParameters().Length == 1 && c.GetParameters()[0].ParameterType == target))
                .OrderByDescending(c => c.GetParameters().Length).First();
            var entry = (target, ctor, ctor.GetParameters());
            PortTypes[type] = entry;
            return entry;
        }
    }

    private TcsSelf.IlPos PortOrigin(SyntaxNode node)
    {
        if (_portOrigins.TryGetValue(node, out var pos)) return pos;
        var span = node.GetLocation().GetLineSpan();
        pos = new TcsSelf.IlPos(span.Path, span.StartLinePosition.Line + 1);
        _portOrigins[node] = pos;
        return pos;
    }
}
