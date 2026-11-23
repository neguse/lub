// tcs2vm is the same as `tcs vm`: the VM backend is the TinyC# implementation (SelfHost/VmBackend), and the CLI is owned by Cli in SelfHost/Tool
return TcsHost.Run(["vm", .. args]);
