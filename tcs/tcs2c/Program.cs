// tcs2c は `tcs c` と同じもの: C backend は TinyC# の実装 (SelfHost/CBackend) で、CLI は SelfHost/Tool の Cli が持つ
return TcsHost.Run(["c", .. args]);
