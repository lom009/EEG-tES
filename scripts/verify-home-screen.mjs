import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";

const source = readFileSync(new URL("../src/App.jsx", import.meta.url), "utf8");
const styles = readFileSync(new URL("../src/styles.css", import.meta.url), "utf8");

assert.match(source, /function HomeScreen\(/, "home screen component should exist");
assert.match(source, /<img className="home-background-image" src="\/assets\/neural-lines\.png"/, "home should use its original static background");
assert.doesNotMatch(source.slice(source.indexOf("function HomeScreen("), source.indexOf("export function App(")), /<video|home-background\.mp4/, "home should not load a background video");
assert.match(source, /onNewExperiment/, "home should expose the new-experiment entry");
assert.match(source, /onExperimentHistory/, "home should expose the experiment-history entry");
assert.match(source, /\["login", "home", "setup", "electrodes", "experiment", "device-lab", "workflow-prototype", "envelope-tacs-prototype", "single-stimulation-demo"\]/, "home, device lab and workflow prototype should be routable application screens");
assert.match(source, /title: "耐受度测试"/, "home should expose the tolerance-test entry");
assert.match(source, /const pathScreen = \{[\s\S]*?"\/experiment": "experiment"/, "direct Render paths should map to application screens");
assert.match(source, /return pathScreen \|\| "home"/, "home should remain the default application screen");
assert.equal(readFileSync(new URL("../public/_redirects", import.meta.url), "utf8").trim(), "/* /index.html 200", "Render should rewrite direct paths to the SPA entry");
const packageJson = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8"));
assert.match(packageJson.scripts.build, /create-static-routes\.mjs/, "production builds should create physical direct-route entries");
assert.match(source, /function BrandHomeButton\(/, "inner screens should share a brand home control");
assert.match(source, /className="brand-home"[\s\S]*?aria-label="返回首页"/, "brand home control should expose an accessible home action");
assert.match(source, /<BrandHomeButton onHome=\{onHome\} \/>/, "shared and experiment headers should render the home control");
assert.match(source, /onHome=\{\(\) => setScreen\("home"\)\}/, "inner screens should route the brand control back home");
assert.match(styles, /\.home-shell\s*\{/, "home should have dedicated responsive styling");
assert.match(styles, /\.brand-home\s*\{[\s\S]*?min-width:\s*158px;[\s\S]*?min-height:\s*48px;/, "brand home control should provide a larger click target");

for (const asset of [
  "home-new-experiment.png",
  "home-records.png",
  "home-title-logo.svg",
  "home-header-logo.svg",
  "neural-lines.png",
]) {
  assert.ok(existsSync(new URL(`../public/assets/${asset}`, import.meta.url)), `${asset} should be stored locally`);
}

console.log("home-screen verification passed");