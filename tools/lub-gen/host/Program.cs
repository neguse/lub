// .NET entry point of lub-gen. The main code is TinyC# (tools/lub-gen/*.cs) and runs on the .NET tcs (Tcs.Program.Run).
// The C lub-gen compiles the same sources to C with tcs c and runs them on tcs's Native/main.c (scripts/build-lub-gen.sh).
return Tcs.Program.Run(args, LubGen.Tool.Arg, LubGen.Tool.Step);
