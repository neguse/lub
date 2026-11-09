# runtime/core

tcs2c の C runtime の本体。tcs2c は生成 C の先頭にこの順で並べる: `tcs_prelude.h`、(runtime/math、fmt、utf8)、
`tcs_core.c`、`tcs_gc.c`、`tcs_collections.c`、`tcs_strings.c`、`tcs_lib.c`。それぞれ単独では compile できない断片で、
関数は生成 C の中の static。

以前は `tcs2c/CRuntime*.cs` の C# の文字列だった。C の file に出したのは、module 単位の混在 (doc/object-model.md §10.2 の X-2)
で tcs VM と同じ runtime (容器と GC) を使うための 1 段目。次の段は、生成 C が参照する記号 (static の root の表など) を
hook にして、これらを VM と一緒に link できる library にすること。
