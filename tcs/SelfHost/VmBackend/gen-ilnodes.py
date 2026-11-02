"""SelfHost/Compiler/Il.cs の record から SelfHost/VmBackend/IlNodes.cs を作る。
IL の node の子の列 (Children) と診断用のテキスト (Dump) を、record の member の順 (Origin を除く) で書く。
Dump の形は旧 C# 版 (tcsvm/Emitter の IlDump.Node、reflection で record を書いたもの) と同じ: lambda の本文の指紋に使う。
使い方: python3 -I SelfHost/VmBackend/gen-ilnodes.py (Il.cs の record を変えたら走らせる)"""
import os
import re

here = os.path.dirname(os.path.abspath(__file__))
src = open(os.path.join(here, '..', 'Compiler', 'Il.cs'), encoding='utf-8').read()

records = []
for m in re.finditer(r'^public record (\w+)\((.*)\)(?: : (\w+))?;', src, re.M):
    name, params = m.group(1), m.group(2)
    fields = []
    for p in [x.strip() for x in params.split(',') if x.strip()]:
        typ, fname = p.rsplit(' ', 1)
        if fname == 'Origin':
            continue
        fields.append((typ, fname))
    records.append((name, fields))

enums = {}
for m in re.finditer(r'^public enum (\w+)\s*\{(.*?)\}', src, re.M | re.S):
    body = re.sub(r'//.*', '', m.group(2))
    enums[m.group(1)] = [x.strip() for x in body.replace('\n', ' ').split(',') if x.strip()]

NODE = {'IlExpr', 'IlStat', 'IlBlock', 'IlTableEntry'}


def base(t):
    return t.rstrip('?')


def children_code(fields):
    out = []
    for typ, f in fields:
        b = base(typ)
        if b in NODE:
            out.append(f'            if (n.{f} != null) r.Add(n.{f});')
        elif b in ('List<IlExpr>', 'List<IlStat>', 'List<IlTableEntry>'):
            out.append(f'            foreach (var x in n.{f}) r.Add(x);')
        elif b == 'List<IlArm>':
            out.append(f'            foreach (var x in n.{f})\n            {{\n                r.Add(x.Cond);\n                r.Add(x.Body);\n            }}')
        elif b == 'List<IlOverride>':
            out.append(f'            foreach (var x in n.{f}) r.Add(x.Value);')
    return out


def dump_expr(typ, f):
    b = base(typ)
    v = f'n.{f}'
    if b in NODE:
        return f'Dump({v})'
    if b == 'string':
        return f'Str({v})'
    if b == 'bool':
        return f'({v} ? "True" : "False")'
    if b == 'int':
        return f'("" + {v})'
    if b in enums:
        return f'{b}Name({v})'
    if b in ('List<IlExpr>', 'List<IlStat>', 'List<IlTableEntry>'):
        return f'Nodes({v})'
    if b in ('List<string>', 'List<string?>'):
        return f'Strs({v})'
    if b == 'List<IlArm>':
        return f'Arms({v})'
    if b == 'List<IlOverride>':
        return f'Overrides({v})'
    raise SystemExit(f'unknown field type {typ} {f}')


lines = []
lines.append('// SelfHost/VmBackend/gen-ilnodes.py が SelfHost/Compiler/Il.cs から作る。手で編集しない')
lines.append('using System.Collections.Generic;')
lines.append('using TcsSelf;')
lines.append('')
lines.append('namespace TcsVm;')
lines.append('')
lines.append('/// <summary>IL の node の子 (Children) と診断用のテキスト (Dump)。member は record の宣言の順 (Origin を除く)。')
lines.append('/// Dump は lambda の本文の指紋 (reload が生きている closure を新版に対応させる鍵) に使う。</summary>')
lines.append('public static class IlNodes')
lines.append('{')
lines.append('    /// <summary>node の子 (IL の式と文、IlBlock、IlTableEntry)。if の腕は条件と本文、with の上書きは値。</summary>')
lines.append('    public static List<object> Children(object node)')
lines.append('    {')
lines.append('        var r = new List<object>();')
first = True
for name, fields in records:
    if name in ('IlArm', 'IlOverride'):
        continue
    code = children_code(fields)
    kw = 'if' if first else 'else if'
    first = False
    lines.append(f'        {kw} (node is {name} n{name})')
    lines.append('        {')
    lines.append(f'            var n = n{name};')
    lines.extend(code)
    lines.append('        }')
lines.append('        return r;')
lines.append('    }')
lines.append('')
lines.append('    /// <summary>node の record の名前 (C# の GetType().Name)。</summary>')
lines.append('    public static string Name(object node)')
lines.append('    {')
for name, fields in records:
    lines.append(f'        if (node is {name}) return "{name}";')
lines.append('        return "?";')
lines.append('    }')
lines.append('')
lines.append('    public static string Dump(object? node)')
lines.append('    {')
lines.append('        if (node == null) return "null";')
for name, fields in records:
    if name in ('IlArm', 'IlOverride'):
        continue
    lines.append(f'        if (node is {name} n{name})')
    lines.append('        {')
    lines.append(f'            var n = n{name};')
    if name == 'IlBlock':
        lines.append('            var stats = new List<string>();')
        lines.append('            foreach (var s in n.Stats) stats.Add(Dump(s));')
        lines.append('            return "{ " + string.Join("; ", stats) + " }";')
    else:
        parts = ' + ", " + '.join(dump_expr(t, f) for t, f in fields) if fields else '""'
        lines.append(f'            return "{name}(" + {parts} + ")";')
    lines.append('        }')
lines.append('        return "?";')
lines.append('    }')
lines.append('')
lines.append('    private static string Str(string? s) => s == null ? "null" : "\\"" + s + "\\"";')
lines.append('')
lines.append('    private static string Strs(List<string?> items)')
lines.append('    {')
lines.append('        var parts = new List<string>();')
lines.append('        foreach (var s in items) parts.Add(Str(s));')
lines.append('        return "[" + string.Join(", ", parts) + "]";')
lines.append('    }')
lines.append('')
for kind in ('IlExpr', 'IlStat', 'IlTableEntry'):
    lines.append(f'    private static string Nodes(List<{kind}> items)')
    lines.append('    {')
    lines.append('        var parts = new List<string>();')
    lines.append('        foreach (var x in items) parts.Add(Dump(x));')
    lines.append('        return "[" + string.Join(", ", parts) + "]";')
    lines.append('    }')
    lines.append('')
lines.append('    // if の腕と with の上書きは C# 版では tuple')
lines.append('    private static string Arms(List<IlArm> items)')
lines.append('    {')
lines.append('        var parts = new List<string>();')
lines.append('        foreach (var x in items) parts.Add("(" + Dump(x.Cond) + ", " + Dump(x.Body) + ")");')
lines.append('        return "[" + string.Join(", ", parts) + "]";')
lines.append('    }')
lines.append('')
lines.append('    private static string Overrides(List<IlOverride> items)')
lines.append('    {')
lines.append('        var parts = new List<string>();')
lines.append('        foreach (var x in items) parts.Add("(" + Str(x.Name) + ", " + Dump(x.Value) + ")");')
lines.append('        return "[" + string.Join(", ", parts) + "]";')
lines.append('    }')
for ename, members in enums.items():
    lines.append('')
    lines.append(f'    public static string {ename}Name({ename} v)')
    lines.append('    {')
    for mbr in members:
        mname = mbr.split('=')[0].strip()
        lines.append(f'        if (v == {ename}.{mname}) return "{mname}";')
    lines.append('        return "" + (int)v;')
    lines.append('    }')
lines.append('}')
open(os.path.join(here, 'IlNodes.cs'), 'w', encoding='utf-8').write('\n'.join(lines) + '\n')
print(f'{len(records)} records, {len(enums)} enums')
