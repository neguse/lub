// Entry point for .NET execution.
// Not used by the tcs VM player: lub passes every *.cs under Game.csproj to tcs, so this is wrapped in the NET symbol, which tcs does not define, to make it look empty.
#if NET
// This entry point runs only on .NET, so the TinyC# analyzer diagnostic (top-level args) does not apply
#pragma warning disable TCS1001
return Lub.App.Run(typeof(Game), args);
#endif
