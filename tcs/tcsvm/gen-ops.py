"""tcsvm/vm/tcsvm_ops.h の TVM_OPS から SelfHost/VmBackend/Op.cs を作る。
使い方: python3 -I tcsvm/gen-ops.py (並びが変わったら走らせる。食い違いは読み込み時の abi で止まる)"""
import os
import re

root = os.path.dirname(os.path.abspath(__file__))
src = open(os.path.join(root, 'vm', 'tcsvm_ops.h'), encoding='utf-8').read()
block = src.split('#define TVM_OPS(X)')[1].split('enum {')[0]
names = re.findall(r'X\((\w+)\)', block)
body = '\n'.join(f'    {n},' for n in names)
# VM が読み込み時に自分の一覧と比べる abi: 名前の列の FNV-1a (32 bit)
h = 2166136261
for n in names:
    for ch in n.encode():
        h = ((h ^ ch) * 16777619) & 0xFFFFFFFF
    h = ((h ^ ord(',')) * 16777619) & 0xFFFFFFFF
signed = h - (1 << 32) if h >= 1 << 31 else h
cs = f'''// tcsvm/gen-ops.py が tcsvm/vm/tcsvm_ops.h から作る。手で編集しない
namespace TcsVm;

/// <summary>命令の一覧。tcsvm/vm/tcsvm_ops.h の TVM_OPS と同じ順 (形式は同 header の注記)。</summary>
public enum Op
{{
{body}
}}

public static class OpAbi
{{
    /// <summary>名前の列の FNV-1a (32 bit の並び)。VM が読み込み時に自分の一覧と比べる。</summary>
    public const int Hash = {signed};
}}
'''
open(os.path.join(os.path.dirname(root), 'SelfHost', 'VmBackend', 'Op.cs'), 'w', encoding='utf-8').write(cs)
print(f'{len(names)} ops')
