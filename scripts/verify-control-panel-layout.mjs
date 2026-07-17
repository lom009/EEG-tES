import assert from "node:assert/strict";
import fs from "node:fs";

const styles = fs.readFileSync(new URL("../src/styles.css", import.meta.url), "utf8");
const app = fs.readFileSync(new URL("../src/App.jsx", import.meta.url), "utf8");

assert.match(
  styles,
  /\.control-panel\s*\{[\s\S]*?overflow:\s*hidden;/,
  "the control panel must remain fixed instead of becoming the scroll container",
);
assert.match(
  styles,
  /\.role-section\s*\{[^}]*flex:\s*0 0 auto;/,
  "the electrode role controls must remain fixed above the impedance results",
);
assert.match(
  styles,
  /\.impedance-section\s*\{[^}]*min-height:\s*0;[^}]*overflow:\s*hidden;/,
  "the impedance section must constrain its scrollable child",
);
assert.match(
  styles,
  /\.impedance-list\s*\{[\s\S]*?overflow-y:\s*auto;/,
  "only the impedance rows should scroll",
);
assert.match(
  styles,
  /\.issue-config\s*\{[\s\S]*?flex:\s*0 0 32px;/,
  "the configuration action must remain fixed at the panel bottom",
);
assert.match(
  app,
  /className="impedance-list"[\s\S]*?role="region"[\s\S]*?tabIndex=\{rows\.length \? 0 : undefined\}/,
  "the scrollable impedance results must be keyboard reachable when rows exist",
);

console.log("control-panel scrolling verification passed");
