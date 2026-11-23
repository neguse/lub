// tcs2c is the same as `tcs c`: the C backend is the TinyC# implementation (SelfHost/CBackend), and the CLI is owned by Cli in SelfHost/Tool
return TcsHost.Run(["c", .. args]);
