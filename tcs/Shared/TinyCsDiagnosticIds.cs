namespace TinyCs;

// TinyC# の準拠の診断の id。TCS1001〜1003 は tcs の compiler (SelfHost/Compiler) が出し、analyzer も同じ compiler を動かして出す
public static class TinyCsDiagnosticIds
{
    public const string UnsupportedSyntax = "TCS1001";

    public const string UnsupportedApi = "TCS1002";

    public const string UnsupportedCollectionNull = "TCS1003";

    // analyzer だけが出す、.NET で実行するときの診断: System.Math の
    // 超越関数は .NET の実装が動き、tcs の経路 (runtime/math) と結果が変わる
    public const string DotnetMath = "TCS1004";
}
