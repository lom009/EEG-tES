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

## Durable stimulation planning and impedance workflow

- The main experiment flow is: patient and experiment information → dedicated stimulation plan page → electrode configuration and detection.
- The dedicated stimulation plan page contains the complete stimulation configuration: paradigm, stimulation mode, paradigm parameters, mode-specific channel parameters, waveform preview, device/protocol constraints, validation and draft saving.
- The electrode page only assigns concrete head positions and physical channels, checks conflicts, and performs stimulation/acquisition impedance detection. It must not repeat stimulation parameter configuration.
- Patient tolerance tests and tolerance-history records are managed from the home page. A new experiment references an applicable completed tolerance record; the electrode page must not run another tolerance test.
- Page 1 saves an `Experiment Draft` before entering stimulation plan configuration. Page 3 remains unavailable until the stimulation plan draft passes all required validation.
- Stimulation impedance remains unavailable until the stimulation plan is valid and the required stimulation electrodes have been assigned.
- Editing the stimulation plan or a stimulation assignment invalidates the prior stimulation impedance result and requires impedance detection again.
- The stimulation paradigm selector on the dedicated plan page is a custom product-styled control; do not replace it with a browser-native `<select>`.
- The tDCS waveform must use the original Figma wave asset and its source coordinate placement rather than an approximated path.

## Durable GitHub synchronization rule

- The canonical remote repository is `https://github.com/lom009/EEG-tES.git` on branch `main`.
- Keep implementation changes local after their relevant tests/build pass. Do not commit or push automatically.
- Only commit and push the accumulated in-scope changes to `origin/main` after the user explicitly asks to “同步 GitHub” or otherwise clearly requests publishing.
- Never commit local dependencies, build output, tool downloads, temporary archives, screenshots, or secrets.
- A successful user-requested push is the trigger for Render to redeploy the public demo automatically.
