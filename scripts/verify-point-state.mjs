import assert from "node:assert/strict";
import {
  applyPointClick,
  getPointVisualState,
  getRowsForRole,
  getSelectedTagsForRole,
  markRoleMeasured,
  removePointAssignment,
} from "../src/pointState.js";

const headPoints = [
  ["FP1"], ["FP2"],
  ["F7"], ["F3"], ["Fz"], ["F4"], ["F8"],
  ["T3"], ["C3"], ["Cz"], ["C4"], ["T4"],
  ["T5"], ["P3"], ["Pz"], ["P4"], ["T6"],
  ["O1"], ["Oz"], ["O2"],
];

const labels = headPoints.map(([label]) => label);

const pointResults = Object.fromEntries(labels.map((label, index) => {
  const tones = [
    [7.2, "优", "excellent"],
    [13, "良", "good"],
    [21, "中", "medium"],
    [34, "差", "poor"],
    [45, "不良", "bad"],
  ];
  return [label, tones[index % tones.length]];
}));

function click(assignments, label, role, polarity = "C") {
  return applyPointClick(assignments, {
    label,
    role,
    polarity,
    result: pointResults[label],
  });
}

function stateMap(assignments) {
  return Object.fromEntries(labels.map((label) => [label, {
    assignment: assignments[label],
    visual: getPointVisualState(assignments[label]),
  }]));
}

function changedLabels(before, after) {
  const beforeMap = stateMap(before);
  const afterMap = stateMap(after);
  return labels.filter((label) => {
    return JSON.stringify(beforeMap[label]) !== JSON.stringify(afterMap[label]);
  });
}

function seedMeasured(role, polarity = "C") {
  let assignments = {};
  labels.forEach((label) => {
    assignments = click(assignments, label, role, polarity);
  });
  return markRoleMeasured(assignments, role);
}

for (const role of ["acquisition", "stimulation"]) {
  for (const label of labels) {
    const before = seedMeasured(role, "C");
    const after = click(before, label, role, "C");
    assert.deepEqual(changedLabels(before, after), [label], `${role}: measured point click should only change ${label}`);
    assert.equal(after[label].measured, false, `${role}: ${label} should become unmeasured selected`);
    assert.equal(getPointVisualState(after[label]), role === "stimulation" ? "stim-c" : "selected");
  }
}

for (const label of labels) {
  const before = seedMeasured("acquisition");
  const after = click(before, label, "stimulation", "C");
  assert.deepEqual(changedLabels(before, after), [label], `cross-role click should only change ${label}`);
  assert.equal(after[label].role, "stimulation");
  assert.equal(after[label].polarity, "C");
  assert.equal(after[label].measured, false);
  assert.equal(getPointVisualState(after[label]), "stim-c");
}

for (const label of labels) {
  const before = seedMeasured("stimulation", "A");
  const after = click(before, label, "stimulation", "C");
  assert.deepEqual(changedLabels(before, after), [label], `polarity switch click should only change ${label}`);
  assert.equal(after[label].role, "stimulation");
  assert.equal(after[label].polarity, "C");
  assert.equal(after[label].measured, false);
  assert.equal(getPointVisualState(after[label]), "stim-c");
}

{
  const before = seedMeasured("stimulation", "C");
  const after = removePointAssignment(before, "F4");
  assert.deepEqual(changedLabels(before, after), ["F4"], "tag removal should only remove F4");
  assert.equal(getPointVisualState(after.F4), "default");
}

{
  const assignments = seedMeasured("stimulation", "C");
  assert.equal(getRowsForRole(headPoints, assignments, "stimulation").length, labels.length);
  assert.equal(getSelectedTagsForRole(headPoints, assignments, "stimulation").length, labels.length);
}

{
  const assignments = seedMeasured("acquisition");
  assert.equal(getPointVisualState(assignments.P4), "excellent");
  assert.equal(
    getPointVisualState(assignments.P4, { hasError: true }),
    "error",
    "a transient point error must override its measured color without mutating the impedance result",
  );
  assert.equal(assignments.P4.tone, "excellent");
  assert.equal(assignments.P4.measured, true);
}

console.log(`point-state verification passed for ${labels.length} points`);
