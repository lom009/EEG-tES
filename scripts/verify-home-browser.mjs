import assert from "node:assert/strict";
import { createRequire } from "node:module";

const require = createRequire(import.meta.url);
const { chromium } = require("/Users/fengyinan/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright");

const browser = await chromium.launch({
  headless: true,
  executablePath: "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
});

try {
  const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
  const runtimeErrors = [];
  page.on("pageerror", (error) => runtimeErrors.push(error.message));

  await page.goto("http://127.0.0.1:5173/", { waitUntil: "networkidle" });
  await page.locator("video.home-background-video").waitFor();
  await page.waitForFunction(() => {
    const video = document.querySelector("video.home-background-video");
    return video && video.readyState >= 2 && !video.paused;
  });
  const videoState = await page.locator("video.home-background-video").evaluate((video) => ({
    muted: video.muted,
    loop: video.loop,
    paused: video.paused,
    readyState: video.readyState,
  }));
  assert.equal(videoState.muted, true, "homepage video should be muted for autoplay");
  assert.equal(videoState.loop, true, "homepage video should loop");
  assert.equal(videoState.paused, false, "homepage video should autoplay");
  await page.getByRole("button", { name: /新建实验/ }).click();
  await page.getByRole("button", { name: "新建实验", exact: true }).waitFor();
  assert.equal(await page.getByRole("button", { name: "新建实验", exact: true }).getAttribute("class"), "is-active");

  await page.goto("http://127.0.0.1:5173/", { waitUntil: "networkidle" });
  await page.getByRole("button", { name: /实验记录/ }).click();
  await page.getByRole("table", { name: "实验历史记录" }).waitFor();

  assert.deepEqual(runtimeErrors, [], "homepage interactions should not emit browser errors");
  console.log("home browser verification passed at 1440x900");
} finally {
  await browser.close();
}
