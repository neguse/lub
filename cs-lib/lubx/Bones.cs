// 実装ライブラリ lubx の TinyC# 版 (haxe-lib/lub/lubx/Bones.hx と対)。
// Haxe 版の mesh.bones (Dynamic、1-based Lua table) は型消去 cast で受ける:
// (List<object>) の要素を 0-based で引き (tcs の List indexer が +1 変換
// するので Haxe 版の 1-based 走査と同じ実効添字)、各要素は
// (Dictionary<string, object>) でキーアクセス、数値は (double) cast。
// tcs の cast は透過 emit なので実行時はそのまま table アクセスになる。
// Haxe 版の nil 番兵ループは List.Count (Lua の #) 上限に置き換える。
// 関数型引数 resolve は Func<> delegate、lua.Table.fromArray は List<double>
// 直返しで不要。

using System;
using System.Collections.Generic;

/// <summary>skinned SDF メッシュ (Sdf の bone() ノード) の bone 行列定型。
/// 規約: shader は float4x4 bones[8]、行列は mesh.bones の並び順、不足分は
/// 単位行列で埋める (Mesh3d の skinned レイアウトとセットで使う)。
/// アニメーション (どの骨をどう回すか) はゲーム側の仕事のまま。</summary>
public static class Bones
{
    /// <summary>最大 bone 数 (shader 側の float4x4 bones[8] と対)。</summary>
    public const int MAX = 8;

    /// <summary>pivot (px, py, pz) 回りの回転 (model 空間)。
    /// T(p) · R · T(−p)。</summary>
    public static Mat4 pivotRot(double px, double py, double pz, Mat4 rot)
    {
        return Mat4.translate(new Vec3(px, py, pz))
            * rot * Mat4.translate(new Vec3(-px, -py, -pz));
    }

    /// <summary>mesh.bones の並び順で resolve(name, x, y, z) が返す行列を
    /// mat4 × 8 = 128 float に詰める。resolve が null を返した bone は
    /// 単位行列。(x, y, z) はその bone の pivot (pivotRot にそのまま
    /// 渡せる)。</summary>
    public static List<double> pack(MeshData? mesh,
        Func<string, double, double, double, Mat4?> resolve)
    {
        var arr = new List<double>();
        int count = 0;
        if (mesh != null && mesh.bones != null)
        {
            var bones = mesh.bones;
            int n = bones.Count;
            int i = 0;
            while (count < MAX && i < n)
            {
                var b = (Dictionary<string, object>)bones[i];
                var m = resolve((string)b["name"], (double)b["x"],
                    (double)b["y"], (double)b["z"]);
                if (m == null)
                    m = new Mat4();
                foreach (var v in m.m)
                    arr.Add(v);
                count++;
                i++;
            }
        }
        while (count < MAX)
        {
            var id = new Mat4();
            foreach (var v in id.m)
                arr.Add(v);
            count++;
        }
        return arr;
    }
}
