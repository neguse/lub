// .NET entry point of lub-gen. The main code is TinyC# (tools/lub-gen/*.cs) and runs on the tcs .NET host (TcsHost).
// The C lub-gen compiles the same sources to C with tcs c and runs them on tcs's SelfHost/Native/host.c (scripts/build-lub-gen.sh).
return TcsHost.Run(args, LubGen.Tool.Start, LubGen.Tool.Step);
