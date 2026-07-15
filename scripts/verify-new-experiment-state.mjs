import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import * as pointState from "../src/pointState.js";

const previousAssignments = {
  F4: {
    role: "acquisition",
    polarity: null,
    value: 8.5,
    statusLabel: "优",
    tone: "excellent",
    measured: true,
  },
  Pz: {
    role: "stimulation",
    polarity: "C",
    value: 9,
    statusLabel: "优",
    tone: "excellent",
    measured: true,
  },
};

assert.equal(
  typeof pointState.createNewExperimentPointAssignments,
  "function",
  "new experiment must expose a point-state reset transition",
);

assert.deepEqual(
  pointState.createNewExperimentPointAssignments(previousAssignments),
  {},
  "new experiment must start with every electrode unassigned",
);

const appSource = readFileSync(new URL("../src/App.jsx", import.meta.url), "utf8");

assert.match(
  appSource,
  /function startNewExperiment\(\)[\s\S]*?setPointAssignments\(createNewExperimentPointAssignments\(\)\)[\s\S]*?setScreen\("electrodes"\)/,
  "the setup-to-electrode transition must clear prior point assignments before entering the workflow",
);

assert.match(
  appSource,
  /onNext=\{startNewExperiment\}/,
  "the new-experiment page must use the reset transition",
);

console.log("new-experiment point-state verification passed");
