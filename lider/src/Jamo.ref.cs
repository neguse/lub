// Declarations of the kana-kanji conversion (jamo) session API (not converted; passed through tcs's --ref).
/// <summary>Kana-kanji conversion (jamo).
/// The implementation is native/jamo/vm_jamo.c, statically linked by the lider player (native/CMakeLists.txt, native and web).
/// When running on lub's prebuilt lub_vm, it loads the neighboring Jamo.so (built by scripts/build-native.sh) as a native extension.
/// There is one engine and one session per process.
/// Clause and candidate numbers count from 0, and positions within the preedit count characters.
/// </summary>
public static class Jamo
{
    /// <summary>Whether jamo is available (true for a player that has the implementation).
    /// </summary>
    public static bool Available() { return false; }

    /// <summary>Starts loading the dictionary (mozc.dict) on a background thread (loading takes a few seconds).
    /// Open waits for loading to finish.
    /// </summary>
    public static bool Preload(string dict) { return false; }

    /// <summary>Loads the dictionary (waits for its result if Preload was called) and creates the session.
    /// </summary>
    public static bool Open(string dict) { return false; }

    /// <summary>Reads the learning data.
    /// </summary>
    public static bool LoadUserData(string path) { return false; }

    /// <summary>Writes the learning data.
    /// </summary>
    public static bool SaveUserData(string path) { return false; }

    /// <summary>true while converting (choosing candidates), false while typing kana.
    /// </summary>
    public static bool Converting() { return false; }

    /// <summary>Adds typed characters to the preedit (romaji becomes kana).
    /// </summary>
    public static bool Input(string text) { return false; }

    /// <summary>Deletes 1 character before the preedit cursor.
    /// Returns true if deleted (the caller then skips the usual Backspace).
    /// </summary>
    public static bool Backspace() { return false; }

    /// <summary>Deletes 1 character after the preedit cursor.
    /// Returns true if deleted.
    /// </summary>
    public static bool DeleteForward() { return false; }

    /// <summary>Cursor position within the preedit.
    /// </summary>
    public static int Cursor() { return 0; }

    /// <summary>Moves the preedit cursor by delta characters.
    /// Returns true if it moved.
    /// </summary>
    public static bool MoveCursor(int delta) { return false; }

    public static bool SetCursor(int pos) { return false; }

    /// <summary>Whether in Latin mode (typed characters are not turned into kana).
    /// </summary>
    public static bool IsAlpha() { return false; }

    public static bool SetAlpha(bool on) { return false; }

    /// <summary>Rewrites the preedit (the focused clause while converting).
    /// kind is 0 = hiragana, 1 = katakana, 2 = half-width Latin, 3 = full-width Latin.
    /// </summary>
    public static bool Transform(int kind) { return false; }

    /// <summary>Uncommitted text.
    /// Returns false if there is none.
    /// </summary>
    public static bool Preedit(out string? text)
    {
        text = null;
        return false;
    }

    /// <summary>Range of the focused clause (while converting) or of the cursor position (while typing kana) within the preedit.
    /// </summary>
    public static void FocusedRange(out int from, out int length)
    {
        from = 0;
        length = 0;
    }

    /// <summary>Converts the preedit.
    /// candidates is the number of candidates made per clause.
    /// </summary>
    public static bool Convert(int candidates) { return false; }

    public static int ClauseCount() { return 0; }

    /// <summary>Reading of the clause.
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

    /// <summary>Number of the candidate selected in the clause.
    /// -1 if none.
    /// </summary>
    public static int Selected(int clause) { return -1; }

    public static bool Select(int clause, int index) { return false; }

    /// <summary>Number of the focused clause.
    /// -1 if not converting.
    /// </summary>
    public static int Focus() { return -1; }

    public static bool SetFocus(int clause) { return false; }

    /// <summary>Changes the length of the focused clause by delta characters and converts again.
    /// </summary>
    public static bool Resize(int delta, int candidates) { return false; }

    /// <summary>Stops conversion and returns to typing kana.
    /// </summary>
    public static bool Revert() { return false; }

    /// <summary>Commits everything and returns the text.
    /// </summary>
    public static bool Commit(out string? text)
    {
        text = null;
        return false;
    }

    /// <summary>Commits up to the focused clause and returns it.
    /// The rest stays in conversion (Converting tells).
    /// </summary>
    public static bool CommitPrefix(out string? text)
    {
        text = null;
        return false;
    }

    /// <summary>Discards the preedit.
    /// </summary>
    public static void Reset() { }
}
