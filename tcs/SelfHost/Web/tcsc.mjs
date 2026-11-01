// セルフホストの tcs compiler の WebAssembly 版の JS の入口 (browser の worker と node)。
//   const tcsc = await createTcsc({ compilerLua, glueLua });   // stage2.lua と web.lua の中身
//   tcsc.open("Game"); tcsc.setSource("Game.cs", text); tcsc.setReference("lub_stub.cs", stub);
//   const r = tcsc.compile();   // { ok, lua, errors, warnings, parsed, revision }
// lua は runtime の前置き (prelude.lua) を含まない。実行する Lua は prelude + lua。
import createModule from "./tcsc-core.mjs";

export async function createTcsc({ compilerLua, glueLua, moduleOptions = {} }) {
  const m = await createModule(moduleOptions);
  const toC = (s) => {
    const n = m.lengthBytesUTF8(s) + 1;
    const p = m._malloc(n);
    m.stringToUTF8(s, p, n);
    return p;
  };
  const err = (() => {
    const g = toC(glueLua), c = toC(compilerLua);
    try {
      return m.UTF8ToString(m._tcsc_load(g, c));
    } finally {
      m._free(g);
      m._free(c);
    }
  })();
  if (err !== "") throw new Error("tcsc: " + err);
  const call = (fn, a, b) => {
    const pf = toC(fn), pa = a === undefined ? 0 : toC(a), pb = b === undefined ? 0 : toC(b);
    try {
      return m.UTF8ToString(m._tcsc_call(pf, pa, pb));
    } finally {
      m._free(pf);
      if (pa) m._free(pa);
      if (pb) m._free(pb);
    }
  };
  const check = (text) => {
    if (text !== "") {
      const r = JSON.parse(text);
      if (r.ok === false) throw new Error("tcsc: " + r.error);
    }
  };
  return {
    open: (entry) => check(call("tcsc_open", entry)),
    setSource: (path, text) => check(call("tcsc_set_source", path, text)),
    setReference: (path, text) => check(call("tcsc_set_reference", path, text)),
    setLibrary: (path, text) => check(call("tcsc_set_library", path, text)),
    remove: (path) => check(call("tcsc_remove", path)),
    compile: () => {
      const r = JSON.parse(call("tcsc_compile"));
      if (r.ok === false && r.error) throw new Error("tcsc: " + r.error);
      return r;
    },
  };
}
