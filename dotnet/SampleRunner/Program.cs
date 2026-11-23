// Finds the sample's entry class (the only class with a static OnFrame) and runs it.
// tests/cs (-p:Tests=true) has many entry classes, so one is chosen with --entry CLASS.
return Lub.App.Run(typeof(Program).Assembly, args);
