// tcs の WebAssembly 版の JS の入口 (playground の tcs VM の増分 session)。
//   const tcs = await createTcs({ toolFiles });   // toolFiles: { "lib/TinySystem/Linq.cs": text, "lib/TinySystem/ValueTuple.cs": text }
//   tcs.vmOpen(entry, files, refs) → {ok, kind, module, batch, restartReasons, errors, revision}   (files と refs は { path: text }。tcs VM の .tcsb。module と batch は base64)
//   tcs.vmUpdate(path, text)       → 同じ形 (kind は batch / restart / error。batch の応答は module を持たない)
//   tcs.vmModule()                 → 最後に通った build の module (base64)
//   tcs.vmConfig(exports, traces)  → 次の vmOpen が entry の他に作る関数 (C.M / C.*) と --trace の path の先頭
//   tcs.vmRemove(path)             → vmUpdate と同じ形 (file を session から外す)
import createCore from "./tcs-core.mjs";

export async function createTcs({ toolFiles }) {
  const m = await createCore();
  m._tcs_web_init();
  const encoder = new TextEncoder();
  const decoder = new TextDecoder();
  // 文字列を wasm の memory に置いて f(ptr, length) を呼ぶ
  const withBytes = (text, f) => {
    const bytes = encoder.encode(text);
    const p = m._malloc(bytes.length + 1);
    m.HEAPU8.set(bytes, p);
    try {
      f(p, bytes.length);
    } finally {
      m._free(p);
    }
  };
  for (const [name, text] of Object.entries(toolFiles))
    withBytes(name, (np, nl) => withBytes(text, (tp, tl) => m._tcs_web_tool_file(np, nl, tp, tl)));
  const call = (args) => {
    m._tcs_web_clear_args();
    for (const a of args) withBytes(String(a), (p, n) => m._tcs_web_push_arg(p, n));
    const p = m._tcs_web_call();
    const n = m._tcs_web_out_length();
    return decoder.decode(m.HEAPU8.subarray(p, p + n));
  };
  const pairs = (map) => Object.entries(map ?? {}).flat();
  return {
    vmOpen(entry, files, refs) {
      const r = Object.entries(refs ?? {});
      return JSON.parse(call(["vmopen", entry, r.length, ...r.flat(), ...pairs(files)]));
    },
    vmUpdate(path, text) {
      return JSON.parse(call(["vmupdate", path, text]));
    },
    vmModule() {
      return call(["vmmodule"]);
    },
    vmConfig(exports, traces) {
      return call(["vmconfig", exports.length, ...exports, traces.length, ...traces]);
    },
    vmRemove(path) {
      return JSON.parse(call(["vmremove", path]));
    },
  };
}
