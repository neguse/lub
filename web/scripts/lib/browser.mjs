// playwright で chromium を立てる共通部。
// verify / golden / tcs の各スクリプトが同じ解決順で同じブラウザを使う。
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { chromium } from "playwright";

/** 実行ファイルの解決順: CHROMIUM_PATH → playwright の版固定 chromium
 * (web/package-lock.json で固定) → system chromium。無ければ undefined
 * (playwright の既定)。 */
export function pickExecutable() {
  const candidates = [
    process.env.CHROMIUM_PATH,
    path.join(
      os.homedir(),
      ".cache/ms-playwright/chromium-1208/chrome-linux64/chrome",
    ),
    "/usr/bin/chromium",
    "/usr/bin/google-chrome",
  ].filter(Boolean);
  for (const p of candidates) {
    try {
      fs.accessSync(p, fs.constants.X_OK);
      return p;
    } catch {}
  }
  return undefined;
}

/** WebGPU を swiftshader (Vulkan) で有効にする引数。golden と verify の描画契約。 */
export const WEBGPU_ARGS = [
  "--enable-unsafe-webgpu",
  "--enable-features=Vulkan,WebGPU",
  "--use-vulkan=swiftshader",
  "--use-angle=swiftshader",
  "--disable-vulkan-fallback-to-gl-for-testing",
];

/**
 * @param {{ headless?: boolean, webgpu?: boolean, executablePath?: string | undefined }} opts
 *   executablePath を明示しなければ pickExecutable() で解決する。
 */
export async function launchChromium({
  headless = true,
  webgpu = false,
  executablePath = pickExecutable(),
} = {}) {
  return chromium.launch({
    headless,
    executablePath,
    args: [...(webgpu ? WEBGPU_ARGS : []), "--no-sandbox"],
  });
}

/** 期限までに終わらなければブラウザを閉じて落とす。戻り値で解除する。 */
export function closeAfter(browser, ms, label) {
  const t = setTimeout(() => {
    console.error(`${label} timed out after ${ms / 1000}s`);
    void browser.close();
  }, ms);
  return () => clearTimeout(t);
}
