// Common code that launches chromium with playwright.
// The verify / tcs scripts use the same browser with the same resolution order.
import fs from "node:fs";
import { chromium } from "playwright";

/** Executable resolution order: CHROMIUM_PATH → the version-pinned chromium of playwright
 * (pinned by web/package-lock.json) → system chromium. If none, undefined
 * (the playwright default). */
export function pickExecutable() {
  const candidates = [
    process.env.CHROMIUM_PATH,
    chromium.executablePath(),
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

/** Arguments that enable WebGPU with swiftshader (Vulkan). The rendering contract of verify. */
export const WEBGPU_ARGS = [
  "--enable-unsafe-webgpu",
  "--enable-features=Vulkan,WebGPU",
  "--use-vulkan=swiftshader",
  "--use-angle=swiftshader",
  "--disable-vulkan-fallback-to-gl-for-testing",
];

/**
 * @param {{ headless?: boolean, webgpu?: boolean, executablePath?: string | undefined }} opts
 *   If executablePath is not given explicitly, it is resolved with pickExecutable().
 */
export async function launchChromium({
  headless = true,
  webgpu = false,
  executablePath = pickExecutable(),
} = {}) {
  return chromium.launch({
    headless,
    executablePath,
    // Audio goes to a fake output (so the Web Audio clock advances even on machines with no audio device or one that does not respond).
    // AudioContext suspend() and resume() complete asynchronously as the spec says (close() rejects promises that have not completed).
    // Always enabled, so behavior does not change with the browser version or experimental settings.
    args: [
      ...(webgpu ? WEBGPU_ARGS : []),
      "--disable-audio-output",
      "--enable-blink-features=AudioContextAsyncStateTransitions",
      "--no-sandbox",
    ],
  });
}

/** If it does not finish by the deadline, closes the browser and fails. The return value cancels it. */
export function closeAfter(browser, ms, label) {
  const t = setTimeout(() => {
    console.error(`${label} timed out after ${ms / 1000}s`);
    void browser.close();
  }, ms);
  return () => clearTimeout(t);
}
