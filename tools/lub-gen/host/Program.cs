// lub-gen の .NET の入口。本体は TinyC# (tools/lub-gen/*.cs) で、tcs の .NET の host (TcsHost) で動かす。
// C の lub-gen は同じソースを tcs c で C にし、tcs の SelfHost/Native/host.c で動かす (scripts/build-lub-gen.sh)。
return TcsHost.Run(args, LubGen.Tool.Start, LubGen.Tool.Step);
