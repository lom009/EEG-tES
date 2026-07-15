export function createPointAssignment({ role, polarity, result }) {
  const [value, statusLabel, tone] = result;
  return {
    role,
    polarity: role === "stimulation" ? polarity : null,
    value,
    statusLabel,
    tone,
    measured: false,
  };
}

export function createNewExperimentPointAssignments() {
  return {};
}

export function applyPointClick(assignments, { label, role, polarity, result }) {
  return {
    ...assignments,
    [label]: createPointAssignment({ role, polarity, result }),
  };
}

export function removePointAssignment(assignments, label) {
  const next = { ...assignments };
  delete next[label];
  return next;
}

export function markRoleMeasured(assignments, role) {
  const next = { ...assignments };
  Object.entries(next).forEach(([label, assignment]) => {
    if (assignment.role === role) {
      next[label] = { ...assignment, measured: true };
    }
  });
  return next;
}

export function hasMeasuredForRole(assignments, role) {
  return Object.values(assignments).some((assignment) => assignment.role === role && assignment.measured);
}

export function getPointVisualState(assignment) {
  if (!assignment) return "default";
  if (assignment.measured) return assignment.tone;
  if (assignment.role === "stimulation") return `stim-${assignment.polarity.toLowerCase()}`;
  return "selected";
}

export function getPointDisplayLabel(label, assignment) {
  if (assignment?.role === "stimulation") return `${label}·${assignment.polarity}`;
  return label;
}

export function getSelectedTagsForRole(headPoints, assignments, role) {
  return headPoints.flatMap(([label]) => {
    const assignment = assignments[label];
    if (!assignment || assignment.role !== role) return [];
    return [{ label, text: getPointDisplayLabel(label, assignment) }];
  });
}

export function getRowsForRole(headPoints, assignments, role) {
  return headPoints.flatMap(([label]) => {
    const assignment = assignments[label];
    if (assignment?.role !== role || !assignment.measured) return [];
    return [{
      channel: getPointDisplayLabel(label, assignment),
      value: `${assignment.value}kΩ`,
      label: assignment.statusLabel,
      tone: assignment.tone,
    }];
  });
}
