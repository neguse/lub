// かな漢字変換 (jamo) の session API の宣言 (変換しない。tcs の --ref で渡る)。
/// <summary>かな漢字変換 (jamo)。
/// 中身は native/jamo/vm_jamo.c で、lider の player (native/CMakeLists.txt、native と web) が静的に link する。
/// lub の既製の lub_vm で動かすときは、隣の Jamo.so (scripts/build-native.sh が作る) を native の拡張として読む。
/// engine と session は process に 1 つ。
/// 文節と候補の番号は 0 から、preedit の中の位置は文字の数で数える。
/// </summary>
public static class Jamo
{
    /// <summary>jamo が使えるか (中身を持つ player なら true)。
    /// </summary>
    public static bool Available() { return false; }

    /// <summary>辞書 (mozc.dict) を裏の thread で読み始める (読むのに数秒かかる)。
    /// Open は読み終わるのを待つ。
    /// </summary>
    public static bool Preload(string dict) { return false; }

    /// <summary>辞書を読み (Preload していればその結果を待ち)、session を作る。
    /// </summary>
    public static bool Open(string dict) { return false; }

    /// <summary>学習の data を読む。
    /// </summary>
    public static bool LoadUserData(string path) { return false; }

    /// <summary>学習の data を書く。
    /// </summary>
    public static bool SaveUserData(string path) { return false; }

    /// <summary>変換中 (候補を選んでいる) なら true、かなを打っている間は false。
    /// </summary>
    public static bool Converting() { return false; }

    /// <summary>打った文字を preedit に足す (ローマ字はかなにする)。
    /// </summary>
    public static bool Input(string text) { return false; }

    /// <summary>preedit の cursor の前の 1 文字を消す。
    /// 消したら true (呼ぶ側は普段の Backspace をしない)。
    /// </summary>
    public static bool Backspace() { return false; }

    /// <summary>preedit の cursor の後の 1 文字を消す。
    /// 消したら true。
    /// </summary>
    public static bool DeleteForward() { return false; }

    /// <summary>preedit の中の cursor の位置。
    /// </summary>
    public static int Cursor() { return 0; }

    /// <summary>preedit の cursor を delta 文字動かす。
    /// 動いたら true。
    /// </summary>
    public static bool MoveCursor(int delta) { return false; }

    public static bool SetCursor(int pos) { return false; }

    /// <summary>英字の mode (打った文字をかなにしない) か。
    /// </summary>
    public static bool IsAlpha() { return false; }

    public static bool SetAlpha(bool on) { return false; }

    /// <summary>preedit (変換中なら focus のある文節) を書き換える。
    /// kind は 0 = ひらがな、1 = カタカナ、2 = 半角英字、3 = 全角英字。
    /// </summary>
    public static bool Transform(int kind) { return false; }

    /// <summary>未確定の文字。
    /// 無ければ false。
    /// </summary>
    public static bool Preedit(out string? text)
    {
        text = null;
        return false;
    }

    /// <summary>preedit の中の、focus のある文節 (変換中) か cursor の位置 (かなを打っている間) の範囲。
    /// </summary>
    public static void FocusedRange(out int from, out int length)
    {
        from = 0;
        length = 0;
    }

    /// <summary>preedit を変換する。
    /// candidates は文節ごとに作る候補の数。
    /// </summary>
    public static bool Convert(int candidates) { return false; }

    public static int ClauseCount() { return 0; }

    /// <summary>文節の読み。
    /// </summary>
    public static bool ClauseYomi(int clause, out string? text)
    {
        text = null;
        return false;
    }

    public static int CandidateCount(int clause) { return 0; }

    public static bool Candidate(int clause, int index, out string? text)
    {
        text = null;
        return false;
    }

    /// <summary>文節で選んでいる候補の番号。
    /// 無ければ -1。
    /// </summary>
    public static int Selected(int clause) { return -1; }

    public static bool Select(int clause, int index) { return false; }

    /// <summary>focus のある文節の番号。
    /// 変換中でなければ -1。
    /// </summary>
    public static int Focus() { return -1; }

    public static bool SetFocus(int clause) { return false; }

    /// <summary>focus のある文節の長さを delta 文字変え、変換し直す。
    /// </summary>
    public static bool Resize(int delta, int candidates) { return false; }

    /// <summary>変換をやめて、かなを打っている状態に戻す。
    /// </summary>
    public static bool Revert() { return false; }

    /// <summary>全部を確定して、その文字を返す。
    /// </summary>
    public static bool Commit(out string? text)
    {
        text = null;
        return false;
    }

    /// <summary>focus のある文節までを確定して返す。
    /// 残りは変換中のまま (Converting で分かる)。
    /// </summary>
    public static bool CommitPrefix(out string? text)
    {
        text = null;
        return false;
    }

    /// <summary>preedit を捨てる。
    /// </summary>
    public static void Reset() { }
}
