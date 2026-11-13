// tcs の compiler の宣言 (変換しない。tcs の --ref で渡る)。
// 中身は native/tcs/vm_tcs.c で、lider の player (native/CMakeLists.txt、native と web) が静的に link する。
// lub の既製の lub_vm で動かすときは、隣の Tcs.so (scripts/build-native.sh が作る) を native の拡張として読む。
// 要求の形は tcs/SelfHost/Tool/Sessions.cs。
// lider は src/Core/TcsSession.cs の形で使う。
public static class Tcs
{
    /// <summary>次の Call に渡す引数を末尾に足す。
    /// </summary>
    public static void Arg(string text) { }

    /// <summary>並べた引数の要求を 1 つ処理し、応答の行を reply に入れて、引数を空にする。
    /// 処理できたら true。
    /// </summary>
    public static bool Call(out string? reply)
    {
        reply = null;
        return false;
    }
}
