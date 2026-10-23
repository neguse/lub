// Frontend startup/queue regression tests. Compiler and player are controlled
// fixtures; bench-tcs separately verifies the real wasm compiler.
import assert from "node:assert/strict";
import { launchChromium, closeAfter } from "./lib/browser.mjs";

const base = process.env.LUB_URL || "http://localhost:5173/";
const browser = await launchChromium();
const clearDeadline = closeAfter(browser, 120000, "TCS startup verify");
try {
  for (const mode of [
    "cold-edit",
    "cold-failure",
    "cold-failure-queued",
    "warm-failure-queued",
    "warm-edit",
    "warm-recovery",
    "restart-failure",
    "player-missing",
  ]) {
    const context = await browser.newContext();
    const page = await context.newPage();
    await page.addInitScript(() => {
      window.__startupMessages = [];
      addEventListener("message", ({ data }) => {
        if (data.type === "startupFixture") window.__startupMessages.push(data);
      });
    });
    await page.route("**/tcs-compiler.ts*", (route) =>
      route.fulfill({
        contentType: "application/javascript",
        body: `
        let opens = 0;
        export async function preloadTcsRuntime() {}
        export async function openTcsSession(files) {
          window.__startupOpens = ++opens;
          await new Promise(r => setTimeout(r, 350));
          if (${JSON.stringify(mode)}.includes("failure") && ${JSON.stringify(mode)} !== "restart-failure" && opens === 1)
            throw new Error("injected startup failure");
          let source = Object.values(files).join("\\n"), revision = 1, updates = 0;
          return {ok:true,errors:[],warnings:[],session:{
            async update(_path, content) {
              if (${JSON.stringify(mode)} === "warm-recovery" && ++updates === 2) throw new Error("injected update failure");
              source=content; return {ok:true,revision:++revision,errors:[],requiresRestart:${JSON.stringify(mode)} === "restart-failure" && ++updates === 2,restartReasons:[]};
            },
            async linkSnapshot() { return "-- fixture revision=" + revision + "\\n" + source; },
            async complete() { return []; },
            async hover() { return {found:false}; }
          }};
        }
      `,
      }),
    );
    await page.route("**/tcs-prebuilt/*.lua", (route) =>
      route.fulfill({
        status:
          mode.startsWith("warm-") ||
          mode === "restart-failure" ||
          mode === "player-missing"
            ? 200
            : 404,
        body: "-- fixture prebuilt",
      }),
    );
    let playerCount = 0;
    await page.route("**/player.html*", (route) => {
      playerCount++;
      const fail = mode === "restart-failure" && playerCount === 2;
      // 最初の player が playerReady を返さない (wasm 未用意、読み込み失敗)。
      const silent = mode === "player-missing" && playerCount === 1;
      return route.fulfill({
        contentType: "text/html",
        body: `<script>
        addEventListener('message',({data})=>{
          if(!data.files)return;
          parent.postMessage({type:'startupFixture',operation:data.type,files:data.files},'*');
          const revision=Number(Object.values(data.files).join('').match(/fixture revision=(\\d+)/)?.[1]);
          if(revision)parent.postMessage({type:'log',msg:'@@tcs_commit '+JSON.stringify({revision,ok:${!fail},commitTimeMs:0})},'*');
        });
        ${silent ? "" : "setTimeout(()=>parent.postMessage({type:'playerReady'},'*'),0);"}
      </script>`,
      });
    });
    await page.goto(base);
    await page.waitForFunction(() => window.__startupOpens === 1);
    if (mode === "player-missing") {
      // player が起動しなくても session は入り (補完 / hover が使える)、起動待ちの編集は次の入力で player の起動からやり直して届く。
      await page.evaluate(() => {
        const hook = window.__lubTest;
        const path = hook.listFiles().find((p) => p.endsWith(".cs"));
        hook.replaceContent(path, hook.getContent(path) + "\n// while booting");
      });
      await page.waitForFunction(
        () =>
          document.querySelector("#status").textContent ===
          "player start failed",
        null,
        { timeout: 30000 },
      );
      assert.ok(await page.evaluate(() => window.__lubTest.csQuery.ready()));
      await page.evaluate(() => {
        const hook = window.__lubTest;
        const path = hook.listFiles().find((p) => p.endsWith(".cs"));
        hook.replaceContent(path, hook.getContent(path) + "\n// after boot");
      });
      await page.waitForFunction(
        () =>
          window.__startupMessages.some(
            (m) =>
              m.operation === "syncFiles" &&
              Object.values(m.files).some(
                (text) =>
                  text.includes("while booting") && text.includes("after boot"),
              ),
          ),
        null,
        { timeout: 4000 },
      );
      console.log(`PASS: ${mode}`);
      await context.close();
      continue;
    }
    if (mode === "cold-failure") {
      await page.waitForFunction(() =>
        document
          .querySelector("#log")
          .textContent.includes("injected startup failure"),
      );
    }
    await page.evaluate(() => {
      const hook = window.__lubTest;
      const path = hook.listFiles().find((p) => p.endsWith(".cs"));
      hook.replaceContent(
        path,
        hook.getContent(path) + "\n// startup queued edit",
      );
    });
    await page.waitForFunction(
      () =>
        window.__startupMessages.some((m) =>
          Object.values(m.files).some((text) =>
            text.includes("startup queued edit"),
          ),
        ),
      null,
      { timeout: 4000 },
    );
    assert.ok(await page.evaluate(() => window.__lubTest.csQuery.ready()));
    const edit = (marker) =>
      page.evaluate((marker) => {
        const hook = window.__lubTest;
        const path = hook.listFiles().find((p) => p.endsWith(".cs"));
        hook.replaceContent(path, hook.getContent(path) + "\n// " + marker);
      }, marker);
    const waitDelivered = (operation, marker) =>
      page.waitForFunction(
        ({ operation, marker }) =>
          window.__startupMessages.some(
            (m) =>
              m.operation === operation &&
              Object.values(m.files).some((text) => text.includes(marker)),
          ),
        { operation, marker },
        { timeout: 4000 },
      );
    if (mode === "warm-recovery") {
      // session の update が落ちたら、入力なしで新 session + fresh player に再試行し、落ちた編集もそのまま届く。以後は warm に戻る。
      await edit("rejected update");
      await waitDelivered("setFiles", "rejected update");
      assert.equal(await page.evaluate(() => window.__startupOpens), 2);
      await edit("recovered update");
      await waitDelivered("syncFiles", "recovered update");
    }
    if (mode === "restart-failure") {
      // fresh player が起動しなければ旧 player を残し、次の編集で再び fresh player を試みる (hot apply には戻らない)。
      await edit("rejected update");
      await page.waitForFunction(
        () =>
          document.querySelector("#status").textContent ===
          "restart failed (old player kept)",
      );
      await edit("recovered update");
      await waitDelivered("setFiles", "recovered update");
    }
    console.log(`PASS: ${mode}`);
    await context.close();
  }
} finally {
  clearDeadline();
  await browser.close();
}
