// Declarations of file system operations (not converted; passed through tcs's --ref).
// The implementation is native/fs/vm_fs.c, statically linked by the lider player (native/CMakeLists.txt, native and web).
// When running on lub's prebuilt lub_vm, it loads the neighboring Fs.so (built by scripts/build-native.sh) as a native extension.
// On the web, the counterpart is emscripten's virtual file system (MEMFS).
// lider uses it in the form of src/Disk.cs.
public static class Fs
{
    /// <summary>Kind (file or directory), size (bytes, capped at 2^31 - 1), and modification time (seconds since 1970).
    /// Returns false if it does not exist.
    /// </summary>
    public static bool Stat(string path, out bool isDir, out int size, out int modified)
    {
        isDir = false;
        size = 0;
        modified = 0;
        return false;
    }

    /// <summary>Returns the names of the directory contents in ascending order of name, separated by newlines (directories have "/" after the name).
    /// Does not return "." and "..".
    /// Returns false if it is not a directory.
    /// </summary>
    public static bool ListDir(string path, out string? names)
    {
        names = null;
        return false;
    }

    /// <summary>Turns path into an absolute path resolved from cwd, without "." and ".." (also follows symlinks).
    /// Returns false for a path that does not exist.
    /// </summary>
    public static bool AbsolutePath(string path, out string? absolute)
    {
        absolute = null;
        return false;
    }

    /// <summary>Deletes a file or an empty directory.
    /// Returns true if deleted, false if it does not exist or cannot be deleted.
    /// </summary>
    public static bool Remove(string path) { return false; }
}
