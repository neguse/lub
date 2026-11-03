// lubx が使う C の実装 (src/lubx/) の .NET 実行の facade。
// cs-lib/lub_stub.cs の namespace Lubx から tools/lub-gen が生成する (手で編集しない。再生成: scripts/gen-api.sh)。
// P/Invoke の宣言は Lub.g.cs の LubNative にある。
#nullable enable
#pragma warning disable CS0649, CS8618, CS0169, CS0414, IDE1006
using System;
using System.Collections.Generic;
using Lub;

namespace Lubx;

/// <summary>Ogg Vorbis の bytes を PCM に戻す (stb_vorbis)。</summary>
public static unsafe partial class Vorbis
{
    /// <summary>Ogg Vorbis の bytes を f32 PCM (channel を交互に並べた frame 列) に戻す。 pcm は frame 有効の view。 Ogg Vorbis でない、または壊れた bytes なら pcm は null。</summary>
    public static void Decode(Bytes data, out Bytes? pcm, out int channels, out int rate)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            LubRuntime.CheckView(data.Frame);
            LubNative.LubView o_pcm = default;
            int o_channels = default;
            int o_rate = default;
            var st = LubNative.lubx_vorbis_decode(LubRuntime.Ctx, data.Ptr, data.Length, &o_pcm, &o_channels, &o_rate);
            if (st == LubNative.LUB_NOT_FOUND)
            {
                pcm = default!;
                channels = default!;
                rate = default!;
                return;
            }
            LubRuntime.Check(st);
            pcm = LubRuntime.View(o_pcm);
            channels = o_channels;
            rate = o_rate;
        }
        finally
        {
            a.End();
        }
    }

}

