"""tcsvm/vm/tcsvm_ops.h の TVM_OPS から tcsvm/Emitter/Op.cs を作る。
使い方: python3 -I tcsvm/gen-ops.py (並びが変わったら走らせる。食い違いは読み込み時の abi で止まる)"""
import os
import re

root = os.path.dirname(os.path.abspath(__file__))
src = open(os.path.join(root, 'vm', 'tcsvm_ops.h'), encoding='utf-8').read()
block = src.split('#define TVM_OPS(X)')[1].split('enum {')[0]
names = re.findall(r'X\((\w+)\)', block)
body = '\n'.join(f'    {n},' for n in names)
cs = f'''// tcsvm/gen-ops.py が tcsvm/vm/tcsvm_ops.h から作る。手で編集しない
namespace TinyCs.Vm;

/// <summary>命令の一覧。tcsvm/vm/tcsvm_ops.h の TVM_OPS と同じ順 (形式は同 header の注記)。</summary>
internal enum Op : byte
{{
{body}
}}

internal static class OpAbi
{{
    /// <summary>名前の列の FNV-1a。VM が読み込み時に自分の一覧と比べる。</summary>
    public static uint Hash()
    {{
        var h = 2166136261u;
        foreach (var name in Enum.GetNames<Op>())
        {{
            foreach (var ch in name) h = (h ^ (byte)ch) * 16777619u;
            h = (h ^ (byte)',') * 16777619u;
        }}
        return h;
    }}
}}
'''
open(os.path.join(root, 'Emitter', 'Op.cs'), 'w', encoding='utf-8').write(cs)
print(f'{len(names)} ops')
