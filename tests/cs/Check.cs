// tests/cs の runtime テストに共通の判定。
// 最初の失敗を出して終了コード 1 で終わる。
// 全部通ったら Check.Pass() で PASS を出して終了コード 0 で終わる。
//
// 期待するエラーは Check.Error(what, want) の後に失敗するはずの呼び出しを置く。
// 呼び出しが失敗すると callback はそこで打ち切られ、entry の OnError (中身は `return Check.OnError(message);`) が message に want が含まれるかを見る。
// 期待していないエラーは失敗。
// 呼び出しが通ってしまったら、次の Check.Frame() (OnFrame の頭で呼ぶ) か Check.Pass() が失敗にする。
using System;
using Lub;

public static class Check
{
    static bool failed;
    static string? expectWhat;
    static string? expectWant;

    public static void That(bool cond, string message)
    {
        if (!cond)
            Fail(message);
    }

    public static void Fail(string message)
    {
        if (failed)
            return;
        failed = true;
        Console.WriteLine("FAIL " + message);
        App.Quit(1);
    }

    public static void Pass()
    {
        Frame();
        if (failed)
            return;
        // 終了コードを受け取れない web の verify (A9) はこの行で合否を見る
        Console.WriteLine("PASS");
        App.Quit(0);
    }

    public static void Error(string what, string want)
    {
        Frame();
        expectWhat = what;
        expectWant = want;
    }

    public static void Frame()
    {
        if (expectWhat != null)
            Fail(expectWhat + ": expected an error with '" + expectWant + "'");
        expectWhat = null;
        expectWant = null;
    }

    public static ErrorAction OnError(string message)
    {
        if (expectWhat == null)
        {
            Fail("unexpected error: " + message);
            return ErrorAction.Abort;
        }
        if (!message.Contains(expectWant!))
            Fail(expectWhat + ": want an error with '" + expectWant + "', got '" + message + "'");
        expectWhat = null;
        expectWant = null;
        return failed ? ErrorAction.Abort : ErrorAction.Log;
    }
}
