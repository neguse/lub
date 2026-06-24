// トレースモード: wsoo glue の env throw スタブを全て「名前を記録して 0 を返す」
// ダミーに置換する。一回の実行で 00_hello compile 中に実際に呼ばれた C primitive を
// 全列挙し、最小 shim 集合(= "shim量")を確定するための spike ツール。
// 返り値は全て 0 なので出力は壊れるが、目的は「呼ばれた名前の集合」の採取。
//   node trace_env.mjs <in haxe.js> <out haxe.trace.js>
import { readFileSync, writeFileSync } from "node:fs";
const [, , inPath, outPath] = process.argv;
let src = readFileSync(inPath, "utf8");

// "name":()=>{throw new\nError("name not implemented")}  を全置換
const re = /"([a-zA-Z_][a-zA-Z0-9_]*)":\(\)=>\{throw new\s+Error\("\1 not implemented"\)\}/g;
let n = 0;
// size 系は正値(8 byte)を返さないと integers lib の init 検証で落ちて
// トレースが途中で止まる。最低限の sensible 返値だけ与えて先へ進める。
src = src.replace(re, (_m, name) => {
  n++;
  let ret = "0";
  if (/_size$/.test(name) || /size_t_size|sizeof|alignmentof|typeof/.test(name)) ret = "8";
  else if (/_max$/.test(name)) ret = "0xffffffff";
  return `"${name}":(...a)=>{(globalThis.__called||(globalThis.__called=new Set())).add(${JSON.stringify(name)});return ${ret}}`;
});

// 実行終了時に呼ばれた集合を書き出すフックを先頭に注入。
const hook = `globalThis.__called=new Set();process.on("exit",()=>{try{require("fs").writeFileSync(${JSON.stringify(outPath + ".called.json")},JSON.stringify([...globalThis.__called].sort(),null,0))}catch(e){}});\n`;
writeFileSync(outPath, hook + src);
console.error(`traced ${n} env stub(s) -> ${outPath}`);
