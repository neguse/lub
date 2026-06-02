// wsoo 生成 glue (haxe.js) の wasm import `env` にある throw スタブ
//   "name":()=>{throw new Error("name not implemented")}
// を、実装で置換する後段パッチャ。再コンパイル不要で高速反復するための spike ツール。
//
//   node patch_env.mjs <in haxe.js> <out haxe.patched.js>
//
// IMPL[name] = 置換する JS 関数式(文字列)。one-shot `--lua` compile で実際に
// 呼ばれた primitive だけをここに足していく(= 最小 shim 集合 = "shim量")。
import { readFileSync, writeFileSync } from "node:fs";

const [, , inPath, outPath] = process.argv;
let src = readFileSync(inPath, "utf8");

// --- 実装テーブル ----------------------------------------------------------
// 文字列を取る primitive の arg ABI は観測して確定する。まず unit->unit の
// init 系を no-op 化。
// 注: pcre2/extc/integers 系は Haxe ソース側で pure-OCaml 化済み(env stub が
// 消えたため、ここには不要)。ここでは threads runtime init だけ no-op 化する。
// pcre2/extc/sha/integers は Haxe ソース/opam pin 側で pure-OCaml 化済み。
// 残るのは OCaml stdlib の systhreads init のみ(`Mutex.create()` が dce.ml 等で
// 多用され threads lib を起動するが pin 不可)。単一スレッドなので no-op で安全。
const IMPL = {
  caml_thread_initialize: "()=>0",
  // ctypes 経路から残る integers の型サイズ問い合わせ(int を返すだけなので
  // env shim でも型不一致は起きない。値は LP64 相当の定数)。init 専用で出力に無影響。
  integers_size_t_size: "()=>8",
  integers_ushort_size: "()=>2",
  integers_uint_size: "()=>4",
  integers_ulong_size: "()=>8",
  integers_ulonglong_size: "()=>8",
  integers_uintptr_t_size: "()=>8",
  integers_intptr_t_size: "()=>8",
  integers_ptrdiff_t_size: "()=>8",
};

// luv の module-init は handle 種別ごとに trampoline getter / version 等の C スタブを
// 呼ぶが、one-shot compile では event loop を回さないため結果は使われない。
// luv 本体の関数も実行時に呼ばれない(trace で確認)。よって残る luv_* スタブは
// 全て 0 を返す no-op で安全。ここで一括 no-op 化する。
{
  // luv 本体は one-shot compile で実行されない(event loop を回さない)。残る luv_*
  // スタブは全て 0 返しで安全。注: ctypes_* のポインタ返却関数は 0 だと wasm の
  // illegal cast になるため blanket には含めない(luv の module-init が ctypes 確保へ
  // 至る経路が byte 一致の最後の壁。README §残作業 参照)。
  // luv の *_trampoline 等は C 関数ポインタ(static_funptr)を返す。0(null)だと
  // ctypes が null funptr を扱う所で unreachable trap になるため、非ゼロのユニーク値を
  // 返す(実際には呼ばれない=libuv の event loop を回さないので値は何でもよい)。
  const re = /"(luv_[a-zA-Z0-9_]+)":\(\)=>\{throw new\s+Error\("\1 not implemented"\)\}/g;
  let n = 0;
  src = src.replace(re, (_m, name) => { n++; return `"${name}":()=>(globalThis.__lc=(globalThis.__lc||0x40000)+8)`; });
  console.error(`blanket no-op luv stubs (unique non-zero): ${n}`);

  // OCaml systhreads / Mutex / Condition は wsoo に無いが、Haxe は単一スレッドで
  // 動く(thread を spawn しない、Mutex.lock/unlock は単一スレッドでは無害)。
  // 全て 0 返しの no-op で安全。
  const reT = /"(caml_(?:thread|mutex|condition)_[a-zA-Z0-9_]+)":\(\)=>\{throw new\s+Error\("\1 not implemented"\)\}/g;
  let m = 0;
  src = src.replace(reT, (_x, name) => { m++; return `"${name}":()=>0`; });
  console.error(`blanket no-op thread/mutex/condition stubs: ${m}`);
}

let patched = 0;
const missing = [];
for (const [name, impl] of Object.entries(IMPL)) {
  // "name":()=>{throw new\nError("name not implemented")}  (改行を跨ぐ)
  const re = new RegExp(
    `"${name}":\\(\\)=>\\{throw new\\s+Error\\("${name} not implemented"\\)\\}`
  );
  if (re.test(src)) {
    src = src.replace(re, `"${name}":${impl}`);
    patched++;
  } else {
    missing.push(name);
  }
}

writeFileSync(outPath, src);
console.error(`patched ${patched} env stub(s); not found in glue: [${missing.join(", ")}]`);
