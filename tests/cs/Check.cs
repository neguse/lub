// Common checks for the tests/cs runtime tests.
// Prints the first failure and exits with exit code 1.
// If everything passes, Check.Pass() prints PASS and exits with exit code 0.
//
// For an expected error, place the call that should fail after Check.Error(what, want).
// When the call fails, the callback stops there, and the entry's OnError (whose body is `return Check.OnError(message);`) checks whether message contains want.
// An unexpected error is a failure.
// If the call succeeds anyway, the next Check.Frame() (called at the start of OnFrame) or Check.Pass() reports a failure.
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
        // The web export page check (web/scripts/export-page-check.mjs), which cannot receive the exit code, uses this line to decide pass or fail
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
