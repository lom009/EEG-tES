import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";

const source = readFileSync(new URL("../src/App.jsx", import.meta.url), "utf8");
const styles = readFileSync(new URL("../src/styles.css", import.meta.url), "utf8");

assert.match(source, /function HomeScreen\(/, "home screen component should exist");
assert.match(source, /<video[\s\S]*?className="home-background-video"/, "home should reserve the supplied-video background layer");
assert.match(source, /poster="\/assets\/neural-lines\.png"/, "home video layer should have a local static poster before the video arrives");
assert.match(source, /<source src="\/assets\/home-background\.mp4" type="video\/mp4" \/>/, "home should use the supplied MP4 background video");
assert.match(source, /muted[\s\S]*?loop[\s\S]*?autoPlay[\s\S]*?playsInline/, "home background video should autoplay silently and loop inline");
assert.match(source, /onNewExperiment/, "home should expose the new-experiment entry");
assert.match(source, /onExperimentHistory/, "home should expose the experiment-history entry");
assert.match(source, /\["home", "setup", "electrodes", "experiment"\]/, "home should be a routable application screen");
assert.match(source, /requestedScreen[\s\S]*?\? requestedScreen : "home"/, "home should be the default application screen");
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
  "home-background.mp4",
]) {
  assert.ok(existsSync(new URL(`../public/assets/${asset}`, import.meta.url)), `${asset} should be stored locally`);
}

console.log("home-screen verification passed");
