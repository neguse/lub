// tcs2vm は `tcs vm` と同じもの: VM backend は TinyC# の実装 (SelfHost/VmBackend) で、CLI は SelfHost/Tool の Cli が持つ
return TcsHost.Run(["vm", .. args]);
