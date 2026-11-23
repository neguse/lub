// JS entry point of the WebAssembly version of tcs (incremental tcs VM sessions for the playground).
//   const tcs = await createTcs({ toolFiles });   // toolFiles: { "lib/TinySystem/Linq.cs": text, "lib/TinySystem/ValueTuple.cs": text }
//   tcs.vmOpen(entry, files, refs) → {ok, kind, module, batch, restartReasons, errors, revision}   (files and refs are { path: text }. tcs VM .tcsb. module and batch are base64)
//   tcs.vmUpdate(path, text)       → same shape (kind is batch / restart / error. A batch reply has no module)
//   tcs.vmModule()                 → module of the last build that passed (base64)
//   tcs.vmConfig(exports, traces)  → functions the next vmOpen builds besides entry (C.M / C.*) and --trace path prefixes
//   tcs.vmRemove(path)             → same shape as vmUpdate (removes the file from the session)
import createCore from "./tcs-core.mjs";

export async function createTcs({ toolFiles }) {
  const m = await createCore();
  m._tcs_web_init();
  const encoder = new TextEncoder();
  const decoder = new TextDecoder();
  // Places a string in wasm memory and calls f(ptr, length)
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
