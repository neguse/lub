/** playground の C# の変換は既定で tcs の wasm (tcs2c で C にした tcs) が行い、補完と hover は .NET の wasm が答える。
 * `?compiler=dotnet` なら変換も .NET の wasm で行う (同じ tcs の実装を .NET で動かしたもの。tcs-prebuilt の cold 起動を使う)。 */
export const useDotnetCompiler =
  typeof location !== "undefined" &&
  new URLSearchParams(location.search).get("compiler") === "dotnet";

/** 既定は C# を tcs VM の bytecode (.tcsb) にして、tcs VM の player (wasm/vm/lub.js) で動かす。
 * 変換は tcs の wasm の VM の session (vmopen / vmupdate)。編集は batch (tvm_apply_batch) で当てるか、fresh player で読み直す。
 * `?runtime=lua` なら deprecate する予定の Lua の player (wasm/lub.js) で動かす。 */
export const useVmRuntime =
  typeof location === "undefined" ||
  new URLSearchParams(location.search).get("runtime") !== "lua";
