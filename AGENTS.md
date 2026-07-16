# EEG-tES Prototype Decisions

## Durable layout rules

- The head model is a shared visual anchor across the entire electrode workflow.
- Acquisition and stimulation pages must use the same head image size and placement.
- All 20 EEG electrode points must use one shared coordinate map on every page and state.
- Switching acquisition/stimulation may change point roles, labels, colors and panel content, but must never move or resize the head model or electrode points.
- Use the acquisition-page head placement as the canonical layout baseline.

## Durable point interaction rules

- All electrode points start unassigned with the white/default treatment.
- Selecting a point for either acquisition or stimulation uses the blue selected treatment.
- Acquisition points change from blue to their impedance-result color only after a completed impedance check.
- Stimulation assignment changes the label/polarity and keeps the blue selected treatment before impedance detection.
- After stimulation impedance detection completes, stimulation points retain the `·A`/`·C` suffix and change to their semantic impedance-result color on both the head model and the impedance list.
- Both acquisition and stimulation visibility controls are checked by default and must not be changed automatically by workflow transitions.
- Visibility controls dim already assigned points of that role to 30% opacity when unchecked and restore full opacity when checked. They must never mutate the assignment or render a dimmed assigned point as an unassigned white/default point.

## Durable stimulation impedance workflow

- Stimulation impedance must remain unavailable immediately after electrode assignment.
- The stimulation CTA opens a centered two-step workflow without moving the shared head model or the right control panel.
- Step 1 is stimulation parameter configuration; step 2 is stimulation tolerance testing.
- Only confirming the tolerance threshold after parameter confirmation may start stimulation impedance detection and reveal stimulation impedance rows.
- Editing a stimulation assignment invalidates the prior stimulation impedance result and requires the two-step workflow again.
- The stimulation type selector in the parameter modal is a custom product-styled dropdown; do not replace it with a browser-native `<select>`.
- The tDCS waveform must use the original Figma wave asset and its source coordinate placement rather than an approximated path.

## Durable GitHub synchronization rule

- The canonical remote repository is `https://github.com/lom009/EEG-tES.git` on branch `main`.
- Keep implementation changes local after their relevant tests/build pass. Do not commit or push automatically.
- Only commit and push the accumulated in-scope changes to `origin/main` after the user explicitly asks to “同步 GitHub” or otherwise clearly requests publishing.
- Never commit local dependencies, build output, tool downloads, temporary archives, screenshots, or secrets.
- A successful user-requested push is the trigger for Render to redeploy the public demo automatically.
