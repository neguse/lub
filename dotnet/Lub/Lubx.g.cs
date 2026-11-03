// lubx が使う C の実装 (src/lubx/) の .NET 実行の facade。
// cs-lib/lub_stub.cs の namespace Lubx から tools/lub-gen が生成する (手で編集しない。再生成: scripts/gen-api.sh)。
// P/Invoke の宣言は Lub.g.cs の LubNative にある。
#nullable enable
#pragma warning disable CS0649, CS8618, CS0169, CS0414, IDE1006
using System;
using System.Collections.Generic;
using Lub;

namespace Lubx;

/// <summary>かな漢字変換 (jamo)。 lub を jamo と build したとき (CMake の LUB_JAMO_DIR) だけ使える。それ以外 (web を含む) では Available が false で、どの関数も何もせず、false、0、null を返す。 engine と session は process に 1 つ。文節と候補の番号は 0 から、preedit の中の位置は文字の数で数える。</summary>
public static unsafe partial class Jamo
{
    /// <summary>この lub が jamo と build してあるか。</summary>
    public static bool Available()
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_available(LubRuntime.Ctx);
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>辞書 (mozc.dict) を裏の thread で読み始める (読むのに数秒かかる)。 Open は読み終わるのを待つ。</summary>
    public static bool Preload(string dict)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_preload(LubRuntime.Ctx, a.Str(dict));
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>辞書を読み (Preload していればその結果を待ち)、session を作る。</summary>
    public static bool Open(string dict)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_open(LubRuntime.Ctx, a.Str(dict));
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>学習の data を読む。</summary>
    public static bool LoadUserData(string path)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_load_user_data(LubRuntime.Ctx, a.Str(path));
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>学習の data を書く。</summary>
    public static bool SaveUserData(string path)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_save_user_data(LubRuntime.Ctx, a.Str(path));
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>変換中 (候補を選んでいる) なら true、かなを打っている間は false。</summary>
    public static bool Converting()
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_converting(LubRuntime.Ctx);
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>打った文字を preedit に足す (ローマ字はかなにする)。</summary>
    public static bool Input(string text)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_input(LubRuntime.Ctx, a.Str(text));
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>preedit の cursor の前の 1 文字を消す。消したら true (呼ぶ側は普段の Backspace をしない)。</summary>
    public static bool Backspace()
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_backspace(LubRuntime.Ctx);
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>preedit の cursor の後の 1 文字を消す。消したら true。</summary>
    public static bool DeleteForward()
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_delete_forward(LubRuntime.Ctx);
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>preedit の中の cursor の位置。</summary>
    public static int Cursor()
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_cursor(LubRuntime.Ctx);
            return r;
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>preedit の cursor を delta 文字動かす。動いたら true。</summary>
    public static bool MoveCursor(int delta)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_move_cursor(LubRuntime.Ctx, delta);
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    public static bool SetCursor(int pos)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_set_cursor(LubRuntime.Ctx, pos);
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>英字の mode (打った文字をかなにしない) か。</summary>
    public static bool IsAlpha()
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_is_alpha(LubRuntime.Ctx);
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    public static bool SetAlpha(bool on)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_set_alpha(LubRuntime.Ctx, (byte)(on ? 1 : 0));
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>preedit (変換中なら focus のある文節) を書き換える。 kind は 0 = ひらがな、1 = カタカナ、2 = 半角英字、3 = 全角英字。</summary>
    public static bool Transform(int kind)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_transform(LubRuntime.Ctx, kind);
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>未確定の文字。無ければ false。</summary>
    public static bool Preedit(out string? text)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            LubNative.LubStr o_text = default;
            var r = LubNative.lubx_jamo_preedit(LubRuntime.Ctx, &o_text);
            text = LubRuntime.StrOrNull(o_text);
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>preedit の中の、focus のある文節 (変換中) か cursor の位置 (かなを打っている間) の範囲。</summary>
    public static void FocusedRange(out int from, out int length)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            int o_from = default;
            int o_length = default;
            LubNative.lubx_jamo_focused_range(LubRuntime.Ctx, &o_from, &o_length);
            from = o_from;
            length = o_length;
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>preedit を変換する。 candidates は文節ごとに作る候補の数。</summary>
    public static bool Convert(int candidates)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_convert(LubRuntime.Ctx, candidates);
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    public static int ClauseCount()
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_clause_count(LubRuntime.Ctx);
            return r;
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>文節の読み。</summary>
    public static bool ClauseYomi(int clause, out string? text)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            LubNative.LubStr o_text = default;
            var r = LubNative.lubx_jamo_clause_yomi(LubRuntime.Ctx, clause, &o_text);
            text = LubRuntime.StrOrNull(o_text);
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    public static int CandidateCount(int clause)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_candidate_count(LubRuntime.Ctx, clause);
            return r;
        }
        finally
        {
            a.End();
        }
    }

    public static bool Candidate(int clause, int index, out string? text)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            LubNative.LubStr o_text = default;
            var r = LubNative.lubx_jamo_candidate(LubRuntime.Ctx, clause, index, &o_text);
            text = LubRuntime.StrOrNull(o_text);
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>文節で選んでいる候補の番号。無ければ -1。</summary>
    public static int Selected(int clause)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_selected(LubRuntime.Ctx, clause);
            return r;
        }
        finally
        {
            a.End();
        }
    }

    public static bool Select(int clause, int index)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_select(LubRuntime.Ctx, clause, index);
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>focus のある文節の番号。変換中でなければ -1。</summary>
    public static int Focus()
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_focus(LubRuntime.Ctx);
            return r;
        }
        finally
        {
            a.End();
        }
    }

    public static bool SetFocus(int clause)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_set_focus(LubRuntime.Ctx, clause);
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>focus のある文節の長さを delta 文字変え、変換し直す。</summary>
    public static bool Resize(int delta, int candidates)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_resize(LubRuntime.Ctx, delta, candidates);
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>変換をやめて、かなを打っている状態に戻す。</summary>
    public static bool Revert()
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            var r = LubNative.lubx_jamo_revert(LubRuntime.Ctx);
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>全部を確定して、その文字を返す。</summary>
    public static bool Commit(out string? text)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            LubNative.LubStr o_text = default;
            var r = LubNative.lubx_jamo_commit(LubRuntime.Ctx, &o_text);
            text = LubRuntime.StrOrNull(o_text);
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>focus のある文節までを確定して返す。残りは変換中のまま (Converting で分かる)。</summary>
    public static bool CommitPrefix(out string? text)
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            LubNative.LubStr o_text = default;
            var r = LubNative.lubx_jamo_commit_prefix(LubRuntime.Ctx, &o_text);
            text = LubRuntime.StrOrNull(o_text);
            return (r != 0);
        }
        finally
        {
            a.End();
        }
    }

    /// <summary>preedit を捨てる。</summary>
    public static void Reset()
    {
        var a = LubRuntime.Arena.Begin();
        try
        {
            LubNative.lubx_jamo_reset(LubRuntime.Ctx);
        }
        finally
        {
            a.End();
        }
    }

}

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

