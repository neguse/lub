// file system の操作の宣言 (変換しない。tcs の --ref で渡る)。
// 中身は native/fs/vm_fs.c で、lider の player (native/CMakeLists.txt、native と web) が静的に link する。
// lub の既製の lub_vm で動かすときは、隣の Fs.so (scripts/build-native.sh が作る) を native の拡張として読む。
// web は emscripten の仮想ファイル (MEMFS) が相手。
// lider は src/Disk.cs の形で使う。
public static class Fs
{
    /// <summary>file かディレクトリの種類、大きさ (byte、2^31 - 1 で頭打ち)、更新時刻 (1970 年からの秒)。
    /// 無ければ false。
    /// </summary>
    public static bool Stat(string path, out bool isDir, out int size, out int modified)
    {
        isDir = false;
        size = 0;
        modified = 0;
        return false;
    }

    /// <summary>ディレクトリの中身の名前を、名前の昇順に改行で区切って返す (ディレクトリは名前の後に "/")。
    /// "." と ".." は返さない。
    /// ディレクトリでなければ false。
    /// </summary>
    public static bool ListDir(string path, out string? names)
    {
        names = null;
        return false;
    }

    /// <summary>path を、cwd から辿った "." と ".." の無い絶対 path にする (symlink も辿る)。
    /// 無い path は false。
    /// </summary>
    public static bool AbsolutePath(string path, out string? absolute)
    {
        absolute = null;
        return false;
    }

    /// <summary>file か空のディレクトリを消す。
    /// 消したら true、無いか消せなければ false。
    /// </summary>
    public static bool Remove(string path) { return false; }
}
