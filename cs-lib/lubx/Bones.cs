// 実装ライブラリ lubx の Bones。
// mesh.Bones は typed な List<SdfBone>。走査は List.Count 上限、resolve は
// Func<> delegate で受ける。

using System;
using System.Collections.Generic;
using static Lub;

/// <summary>skinned SDF メッシュ (Sdf の bone() ノード) の bone 行列定型。
/// 規約: shader は float4x4 bones[8]、行列は mesh.bones の並び順、不足分は
/// 単位行列で埋める (Mesh3d の skinned レイアウトとセットで使う)。
/// アニメーション (どの骨をどう回すか) はゲーム側の仕事のまま。</summary>
public static class Bones
{
    /// <summary>最大 bone 数 (shader 側の float4x4 bones[8] と対)。</summary>
    public const int Max = 8;

    /// <summary>pivot (px, py, pz) 回りの回転 (model 空間)。
    /// T(p) · R · T(−p)。</summary>
    public static Mat4 PivotRot(double px, double py, double pz, Mat4 rot)
    {
        return Mat4.Translate(new Vec3(px, py, pz))
            * rot * Mat4.Translate(new Vec3(-px, -py, -pz));
    }

    /// <summary>mesh.bones の並び順で resolve(name, x, y, z) が返す行列を
    /// mat4 × 8 = 128 float に詰める。resolve が null を返した bone は
    /// 単位行列。(x, y, z) はその bone の pivot (pivotRot にそのまま
    /// 渡せる)。</summary>
    public static List<double> Pack(MeshData? mesh,
        Func<string, double, double, double, Mat4?> resolve)
    {
        var arr = new List<double>();
        int count = 0;
        if (mesh != null && mesh.Bones != null)
        {
            var bones = mesh.Bones;
            int n = bones.Count;
            int i = 0;
            while (count < Max && i < n)
            {
                var b = bones[i];
                var m = resolve(b.Name, b.X, b.Y, b.Z);
                if (m == null)
                    m = new Mat4();
                foreach (var v in m.M)
                    arr.Add(v);
                count++;
                i++;
            }
        }
        while (count < Max)
        {
            var id = new Mat4();
            foreach (var v in id.M)
                arr.Add(v);
            count++;
        }
        return arr;
    }
}
