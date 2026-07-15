# Design QA

- Source visual truth: `docs/figma-43-6052.png`, `docs/figma-43-6200.png`, `docs/figma-43-6770.png`, `docs/figma-43-6377.png`, `docs/figma-43-7298.png`, and Figma MCP inline screenshots for nodes `55:1623` and `70:2022`
- Source nodes: Figma `1sFpUX3GlXOiFhODJu8GRK`, nodes `43:6052`, `43:6200`, `43:6770`, `43:6377`, `43:7298`, `55:1623`, and `70:2022`
- Implementation: `http://127.0.0.1:5173/`
- Target viewport: 1440 × 900 for electrode workflow; 1082 × 1044 in-app browser viewport for TI modal QA; 1440 × 900 in-app browser viewport for HD modal QA
- Same-size browser comparisons: `docs/prototype-43-6377-crop.png` (560 × 527) and `docs/prototype-43-7298-crop.png` (611 × 530)
- Latest implementation evidence: `/private/tmp/eeg-tes-ti-full.png` and `/private/tmp/eeg-tes-hd-full.png`
- Reviewed browser evidence: current annotation capture at 2240 × 926, browser-rendered stimulation result at 1440 × 900, TI parameter modal at 1082 × 1044, and HD parameter modal at 1440 × 900
- State: acquisition results, stimulation assignment, two-step stimulation setup, stimulation impedance result, TI stimulation-parameter configuration modal, and HD stimulation-parameter configuration modal

## Full-view comparison evidence

The current 2240 × 926 annotation evidence confirms the earlier responsive fixes: the back label remains single-line, the head model stays centered in the workspace, and the visibility controls remain anchored beside the right panel. The 1440 × 900 browser capture confirms the same shared head geometry and the completed stimulation-result state. The latest 1082 × 1044 in-app browser capture confirms that the TI parameter modal renders at the source 560 × 748 size and preserves the underlying shared head/control-panel placement. The HD browser capture confirms the HD parameter modal renders at the source 560 × 618 size without introducing a waveform area or moving the shared electrode workspace underneath.

## Focused comparison evidence

- Stimulation result identity: the browser-rendered `T6·C` state shows the same suffix in the selected tag, head point, and impedance row.
- Stimulation result color: `T6·C` changes from blue assignment styling to the green `优` result styling on the head model while the row shows `8.5kΩ / 优`.
- TI parameter modal: Figma node `55:1623` shows the custom `TI` type dropdown, dual-line TI waveform, summary values `40 Hz` and `1.50 MA`, channel A/B cards, and matching default values `980 Hz` and `1.8 mA`. The latest browser annotation removed the earlier `Space` key-hint requirement. The implementation screenshot `/private/tmp/eeg-tes-ti-full.png` matches those visible contents and the source modal dimensions.
- HD parameter modal: Figma node `70:2022` shows the custom `HD` type dropdown, no waveform preview, a 512 × 328 HD configuration panel, default current `0.5`, an apply-all control, Ch1–Ch7 current rows, and `2 mA` row values. The implementation screenshot `/private/tmp/eeg-tes-hd-full.png` matches those visible contents and the 560 × 618 source modal dimensions.
- Earlier responsive fixes remain visible in the current wide annotation evidence; no P0/P1/P2 regression was found.

## Comparison history

### Iteration 1

- P2: Back control text wrapped at 2240 × 926.
  - Fix: increased its width and minimum width to 148px and enforced `white-space: nowrap`.
- P1: Head stage remained fixed to the left at wide viewport sizes.
  - Fix: changed horizontal placement to center within the workspace excluding the 400px control panel.
- P2: Visibility controls remained at a fixed x-coordinate instead of following the workspace edge.
  - Fix: anchored the controls 460px from the right, keeping them next to the control panel.
- Post-fix evidence: production build passes; a fresh browser-rendered screenshot has not yet been captured.

### Iteration 2

- Added the acquisition result state from node `43:6200`: five exact impedance readings, refresh control, measured point colors, title and filter state.
- Added the stimulation assignment state from node `43:6770`: Pz·A/T4·C assignments, cathode selection, stimulation-specific empty state, CTA and head positioning.
- Connected all three source states into one interaction chain instead of separate static screens.
- Post-fix evidence: production build passes; fresh annotated browser evidence is still pending.

### Iteration 3

- Browser evidence at 1440 × 900 showed the legacy anterior/posterior point map, mixed acquisition/stimulation assignments, and an unboxed/misaligned legend.
- Replaced all 20 electrode coordinates with the exact Figma point-layer metadata from nodes `55:1432` and `55:1556`.
- Aligned the acquisition point layer to `(279,192)` and stimulation point layer to `(279,141)` through the matching head-stage offsets.
- Reset role transitions to the Figma snapshots so acquisition does not retain Pz·A/T4·C or user-added points from the stimulation state.
- Corrected the legend container, header pill widths/gaps, filter anchor and stimulation panel vertical rhythm.
- Post-fix evidence: production build passes; a fresh browser annotation after a full page reload is pending.

### Iteration 4

- Product decision: acquisition and stimulation must share one canonical head-model and electrode coordinate system across every screen.
- Removed the stimulation-specific vertical head offset. Role switching now changes only states, labels, colors and panel content.
- Recorded the shared-layout rule in `AGENTS.md` so future screens do not reintroduce role-specific head movement.

### Iteration 5

- Product interaction rule: all points start white; acquisition and stimulation selections are blue; only completed acquisition impedance results use semantic result colors.
- Removed all preselected default points and automatic role-switch assignments.
- Changed impedance rows and result colors to derive from the user's actual acquisition selections.
- Kept both role-visibility controls checked by default and removed automatic workflow toggling.

### Iteration 6

- Corrected role visibility behavior: unchecking a role now removes its assigned markers from view without changing their stored assignment or status.
- Rechecking restores the exact prior blue selection or impedance-result color instead of showing a white/default marker.

### Iteration 7

- Updated the visibility treatment from fully hidden to 30% opacity per product feedback.
- Dimmed points retain their exact role, polarity and impedance-result styling; rechecking restores full opacity.

### Iteration 8

- Added the Figma `43:6377` stimulation-parameter modal at its exact 560 × 527 geometry, including warning bar, two-step indicator, tDCs waveform, current/ramp controls and confirmation action.
- Added the Figma `43:7298` tolerance-test modal at its exact 611 × 530 geometry, including 40 μA step controls, 2 mA limit, history import and threshold confirmation action.
- Locked stimulation impedance detection behind the ordered parameter → tolerance flow; the refresh control and result rows are unavailable until both steps complete.
- Verified in the in-app browser at 1440 × 900: the stimulation CTA opens step 1, confirmation advances to step 2, and threshold confirmation closes the modal and reveals the selected stimulation point's impedance row.
- Matched the source modals' centered placement, dimensions, spacing, overlay treatment, typography, colors and control hierarchy; updated the tDCs waveform to the source's two-plateau profile.

### Iteration 9

- Corrected stimulation result identity: every impedance row now retains the selected polarity suffix (`·A` or `·C`), for example `T6·C`.
- Synchronized the head model with stimulation impedance results: measured stimulation points retain their polarity suffix and switch from blue assignment styling to the matching semantic impedance color.
- Editing a stimulation assignment still invalidates the prior result and returns all stimulation points to the blue pre-detection state.
- Post-fix browser evidence: `T6·C` is exposed as `excellent` on the head model and appears as `T6·C / 8.5kΩ / 优` in the stimulation impedance list; browser console reports no errors.

### Iteration 10

- Replaced the browser-native stimulation-type select with the 122 × 32 product-styled dropdown from Figma node `43:6377`, including the 20px source arrow and custom option layer.
- Replaced the approximated tDCS path with the original Figma waveform asset and aligned the grid, axis labels and data layer to the source coordinate-system insets.
- Browser evidence: `docs/prototype-43-6377-dropdown-wave-crop.jpg` matches the 560 × 527 source geometry; the DOM-verified custom dropdown exposes four styled options and no longer invokes the native browser menu.

### Iteration 11

- Read Figma node `55:1623` through the Figma MCP and used its inline screenshot plus geometry metadata as the TI modal source: 560 × 748, 24px padding, 16px vertical rhythm, 100px TI waveform area, 58px summary card, two 128px channel cards, and 32px primary CTA.
- Corrected the TI default state to match the source visual: channel A and channel B both render `980 Hz` and `1.8 mA`; the summary renders the source `40 Hz` and `1.50 MA` while preserving live slider interactivity after edits.
- Added the `Space` key hint inside the TI modal confirmation button during the initial TI pass; this was later removed based on browser annotation feedback.
- Used the original Figma TI waveform asset at `/assets/ti-wave-source.svg` instead of an approximated inline path.
- Browser evidence: `/private/tmp/eeg-tes-ti-full.png` shows the modal open in the in-app browser with the custom TI dropdown, dual-line waveform, summary values, channel A/B cards, default values, and CTA. Browser console reports no errors.
- Source-file export note: Figma MCP inline screenshot capture succeeded, but writing the exported Figma PNG to `/private/tmp/eeg-tes-ti-figma.png` was rejected by the connector safety layer. The QA comparison used the successfully captured inline Figma screenshot and browser-rendered implementation evidence instead of a local source PNG file.

### Iteration 12

- Removed the `Space` key hint from the TI modal confirmation CTA based on browser annotation feedback.
- Replaced the TI summary-card PNG icons with the user-provided SVG assets:
  - `Frame 15694.svg` -> `/assets/icon-ti-interference-frequency.svg`
  - `Frame 15693.svg` -> `/assets/icon-ti-envelope-current.svg`
- Browser recheck confirms the TI modal button text is `确认参数配置`, no `.stim-workflow-keyhint` node remains, and the summary icons load from the two new SVG paths. Console logs are clean.
- Production build passes after the icon and CTA cleanup.

### Iteration 13

- Read Figma node `70:2022` through the Figma MCP using the read-only design-context and screenshot tools. The source frame is 560 × 618 and contains the HD-specific parameter layout.
- Added the HD modal branch with the custom `HD` type dropdown, no waveform preview, a 512 × 328 configuration panel, default current input `0.5`, an apply-all control, Ch1–Ch7 rows, `2 mA` default row values, and the `确认参数配置` CTA.
- Added the Figma HD chevron SVG assets:
  - `/assets/icon-hd-chevron-up.svg`
  - `/assets/icon-hd-chevron-down.svg`
- The exact Figma apply-all icon asset download was rejected by the connector safety layer, so the implementation uses the existing lucide `CircleArrowRight` icon as a controlled fallback while preserving the Figma layout, color and spacing.
- Browser recheck confirms the HD modal is 560 × 618, the HD panel is 512 × 328, no waveform node is rendered in HD mode, Ch1–Ch7 values default to `2`, and console logs are clean.
- Production build passes after the HD modal implementation.

### Iteration 14

- Investigated the browser annotation where a default F4 point appeared to trigger broad point-state changes after stimulation results were visible.
- Root cause: stimulation/acquisition result rendering depended on broad issued booleans (`stimulationIssued` / `configIssued`) without verifying that the current point assignment set still matched the set measured during impedance detection.
- Interim attempt: assignment signatures invalidated the whole result set, which prevented some stale global-state bugs but still allowed one point click to change unrelated points. This was superseded by Iteration 15.

### Iteration 15

- Read Figma node `7:32` for the point-state component variants: default, selected, acquisition result states, stimulation anode selected/result states, and stimulation cathode selected/result states.
- Extracted point behavior into `src/pointState.js` so default/selected/stimulation/result visual states, row generation, tag generation, clicking, deleting, and marking measured all use one point-level state machine.
- New invariant: clicking a point changes only that point. Other point assignments, measured flags, roles, polarity and result colors remain unchanged.
- Head-model clicks now always set/re-set the clicked point to the current role/current polarity selected state with `measured: false`; deletion is only through the selected-tag `×`.
- Added `scripts/verify-point-state.mjs` and `npm run test:point-state`. It verifies all 20 points across acquisition measured-click, stimulation measured-click, acquisition-to-stimulation cross-role click, stimulation polarity switch, tag deletion, row generation and tag generation.
- Browser sanity recheck confirms the core cross-role path: after P3/P4 acquisition impedance succeeds, switching to stimulation and clicking P4 changes only P4 to `P4·C stim-c`; P3 remains `excellent`, stimulation rows are empty, and console logs are clean.

### Iteration 16

- Replaced the tolerance-step completed icon for `刺激参数配置` with the user-provided SVG asset `Frame 15768.svg`, stored as `/assets/icon-step-complete.svg`, and removed the previous Lucide completed icon from that step.
- Read the Figma MCP source for `Pages: 实验中` via node `75:2051`; the page contains 8 target frames: `待机中`, `自动模式/待机中`, `开始采集`, `消隐`, `刺激待开始`, `刺激中`, `恢复进行中`, and `完结/可导出数据`.
- Cached all 8 Figma screenshots locally under `.codex/figma/` for source comparison and downloaded the five real Figma waveform vector assets into `/assets/experiment-wave-*.svg`.
- Added the post-impedance experiment workspace and connected it to the stimulation tolerance flow. This was later corrected in Iteration 18 so tolerance confirmation runs stimulation impedance detection first, and the user enters the experiment stage only through the enabled top-right next action after valid stimulation impedance rows are available.
- Implemented the experiment state machine: `待机中 → 采集进行中 → 消隐 → 刺激待开始 → 刺激中 → 恢复进行中 → 已完成`, plus emergency stop, export data, manual/auto mode switch, return to electrode configuration, and live right-panel status changes.
- Browser verification: the full electrode-to-experiment path succeeds; all experiment phases advance in order; the finished state exposes exactly one `导出实验数据` action; returning from the experiment page restores the electrode configuration page.
- Layout verification in the in-app browser at `1247 × 1044`: status strip renders at y=88, signal card at y=140, right panel at y=88, with no horizontal or vertical overflow. Browser console error logs are clean.
- Build verification: production Vite build passes, and `scripts/verify-point-state.mjs` still passes for all 20 electrodes after the experiment-page work.

### Iteration 17

- Fixed the responsive width of the experiment `实时信号监测` card by removing the hard `max-width: 976px` cap. The card now follows the existing width formula `calc(100vw - 464px)`, preserving left margin 24px, right panel width 400px, right margin 24px, and the 16px gap between signal card and run panel.
- Browser verification at `2240 × 926`: signal card width is `1776px`, right panel width remains `400px`, gap is `16px`, and there is no horizontal or vertical overflow.
- Browser verification at `1280 × 720`: signal card width is `816px`, right panel width remains `400px`, gap is `16px`, and there is no horizontal or vertical overflow.
- Build verification: production Vite build passes, and `scripts/verify-point-state.mjs` still passes for all 20 electrodes after the responsive width fix.

### Iteration 18

- Corrected the stimulation tolerance workflow based on browser annotation feedback: `确认阈值并进行阻抗检测` now closes the tolerance modal and runs stimulation impedance detection while staying on the electrode page. It no longer jumps directly into the experiment stage.
- The top-right next action is now gated by valid stimulation impedance results. Before stimulation impedance detection it remains disabled with `下一步：耐受测试`; after stimulation impedance rows appear it becomes enabled and changes to `下一步：进行实验`.
- Browser verification in the in-app preview confirms the corrected sequence: stimulation point `P4` selected as cathode, parameter confirmation completed, tolerance confirmation clicked, page stayed on `main.electrode-shell`, stimulation impedance row `P4·C 9kΩ 优` appeared, top-right next became enabled with `下一步：进行实验`, and clicking it entered `main.experiment-shell`.
- Build verification: production Vite build passes, and `scripts/verify-point-state.mjs` still passes for all 20 electrodes after the stimulation gate correction.

### Iteration 19

- Replaced the five `运行监控` summary-row icons with the user-provided SVG assets:
  - `Frame 15759.svg` -> `/assets/icon-run-stage.svg`
  - `Frame 15759-1.svg` -> `/assets/icon-run-current.svg`
  - `Frame 15759-2.svg` -> `/assets/icon-run-impedance.svg`
  - `Frame 15759-3.svg` -> `/assets/icon-run-duration.svg`
  - `Frame 15759-4.svg` -> `/assets/icon-run-alert.svg`
- Updated the run summary markup from Lucide inline icons to real `<img>` assets and preserved the existing 20 × 20 icon footprint and row alignment.
- Build verification: production Vite build passes, and the built `dist/assets/` output includes all five new `icon-run-*.svg` files.
- Point-state regression verification still passes for all 20 electrodes.
- Browser verification note: the in-app browser check for this specific icon replacement was blocked by the tool safety layer with a policy message that `http://127.0.0.1:5173` must not be used. No browser-side visual pass is claimed for Iteration 19.

### Iteration 20

- Connected to the local Figma MCP server at `127.0.0.1:3845/mcp` after sandbox escalation and confirmed the server exposes `get_design_context`, `get_metadata`, `get_screenshot`, `get_variable_defs`, and `get_figjam`.
- Read Figma node `75:2120` (`时序控制`) through local MCP `get_design_context`; the source structure shows the first and third unit controls as white dropdowns with an arrow icon, while the second and fourth duration/unit controls are gray disabled controls without an arrow.
- Downloaded the Figma MCP-provided sequence assets into local project assets:
  - `/assets/icon-sequence-arrow-down.svg`
  - `/assets/icon-sequence-time-fill.svg`
  - `/assets/icon-sequence-play-fill.svg`
- Updated the experiment sequence table to match the Figma source: first and third unit controls use a dropdown-style arrow, second and fourth controls are disabled gray, waiting statuses use the Figma 14px time icon instead of a CSS dot, the start action uses the Figma play-fill icon, and the first sequence index uses the wider 18 × 16 badge shown in the source.
- Verification per user instruction: browser verification was intentionally skipped. Production Vite build passes, and `scripts/verify-point-state.mjs` still passes for all 20 electrodes.

### Iteration 21

- Read the Figma MCP context for experiment node `75:3639` (`开始采集`) and the nested `时序控制` block `75:3703`.
- Downloaded and applied the real Figma assets for the running sequence state and run-ring artwork:
  - `/assets/icon-sequence-loader.svg`
  - `/assets/run-ring-wave-active.svg`
  - `/assets/run-ring-inner-track.svg`
  - `/assets/run-ring-outer-track.svg`
  - `/assets/run-ring-active-arc.svg`
  - `/assets/run-ring-active-dot.svg`
- Updated the running sequence row to match the source: white row with green dashed border, and the right-side `进行中` status rendered as a green 94 × 32 button with the Figma loader icon.
- Replaced the previous CSS conic run ring with the Figma layered ring structure: 115 × 115 wrapper, 97 × 97 centered card, inner/outer ring assets, active arc, endpoint dot, and source waveform icon.
- Verification: production Vite build passes; local preview `http://127.0.0.1:5173/` returns HTTP 200; `scripts/verify-point-state.mjs` still passes for all 20 electrodes.

### Iteration 22

- Read Figma node `81:6752` (`Frame 15768`) for the experiment page upper-right status indicator. The source component is a 138 × 138 frame with separately addressable layers.
- Downloaded the Figma MCP-provided layer assets:
  - `/assets/run-status-inner.svg`
  - `/assets/run-status-outer.svg`
  - `/assets/run-status-wave-standby.svg`
  - `/assets/run-status-rotor.svg`
- Rebuilt the experiment run status indicator as static layered DOM rather than a flattened image. The layers are exposed as `run-ring-inner`, `run-ring-outer`, `run-ring-wave`, `run-ring-label`, and `run-ring-rotor` so future animation attributes can be applied to the matching layer directly.
- Preserved the existing dynamic experiment status text while aligning the static geometry to the Figma source: 138 × 138 wrapper, 118px inner ring at `(10,10)`, 134px outer/rotor at `(2,2)`, and the waveform at `(51.75,46)`.
- Verification: production Vite build passes; local preview `http://127.0.0.1:5173/` returns HTTP 200; `scripts/verify-point-state.mjs` still passes for all 20 electrodes.

### Iteration 23

- Applied the user-provided waveform path-trim keyframe `kf_75_2096_path-trim_0` to the experiment status indicator's center waveform.
- Converted the center waveform layer from an external `<img>` to an inline SVG using the exact Figma asset path data, because CSS cannot target a `path` inside an externally referenced SVG image.
- Scoped the animation to active experiment phases only: it runs outside `standby`, `finished`, and `stopped`, while the standby waveform remains static.
- Verification: production Vite build passes; local preview `http://127.0.0.1:5173/` returns HTTP 200; `scripts/verify-point-state.mjs` still passes for all 20 electrodes.

### Iteration 24

- Removed the bottom `进入下一阶段` manual action from active experiment phases, matching the design feedback that this operation should not exist.
- Added automatic experiment phase progression after the user starts a phase:
  - `采集进行中` automatically advances to `消隐`
  - `消隐` automatically advances to `刺激待开始`
  - `刺激中` automatically advances to `恢复进行中`
  - `恢复进行中` automatically advances to `已完成`
- Preserved the manual start points where the workflow requires user action: standby still starts acquisition from the first sequence row, and `刺激待开始` still starts stimulation from the third sequence row.
- Verification: production Vite build passes; static source check confirms `进入下一阶段` and `phase-next-button` are no longer present; local preview `http://127.0.0.1:5173/` returns HTTP 200; `scripts/verify-point-state.mjs` still passes for all 20 electrodes.

### Iteration 25

- Read Figma node `81:6758` for the green active/running experiment status indicator.
- Downloaded the green Figma assets for the active indicator:
  - `/assets/run-status-rotor-active.svg`
  - `/assets/run-status-wave-active.svg`
- Updated the running indicator color behavior so actual running phases (`采集`, `消隐`, `刺激`, `恢复`) use green text and green center waveform stroke.
- Switched the `旋转外环` layer to the green Figma rotor asset during running phases and added a linear rotation animation to that exact layer via `run-status-rotor-spin`.
- Kept waiting/non-running states, including `待机中` and `刺激待开始`, from being treated as running indicators.
- Verification: production Vite build passes; local preview `http://127.0.0.1:5173/` returns HTTP 200; `scripts/verify-point-state.mjs` still passes for all 20 electrodes.

### Iteration 26

- Read Figma MCP node `75:2776` (`刺激待开始`) for the completed sequence state and bottom timeline overview.
- Rebuilt the signal timeline overview to match the Figma two-row structure: a 28px colored phase label row and a 28px white duration row, using the source ratios and colors (`采集 #e2eeff/#2f86ff`, `消隐 #ffece7/#fd7557`, `刺激 #f3e8ff/#ab59fc`, `恢复 #e3f2ea/#38a169`).
- Updated the completed sequence status to use a dedicated green check-circle asset (`/assets/icon-sequence-done-fill.svg`) with the existing 14px inline status geometry, instead of reusing the waiting-state clock icon.
- Verification: production Vite build passes; local preview `http://127.0.0.1:5173/` returns HTTP 200; `scripts/verify-point-state.mjs` still passes for all 20 electrodes.

### Iteration 27

- Read Figma MCP node `81:6980` for the finished experiment action button.
- Replaced the finished-state `导出实验数据` action with the source button copy `结束实验`.
- Downloaded and applied the exact Figma check-fill asset as `/assets/icon-finish-experiment-check-fill.svg`.
- Updated the finished action styling to match the source: primary `#3941b6`, 32px height, 6px radius, 8px gap, 20px icon, and Inter 14px semibold white label.
- Verification: production Vite build passes; local preview `http://127.0.0.1:5173/` returns HTTP 200; `scripts/verify-point-state.mjs` still passes for all 20 electrodes.

### Iteration 28

- Read Figma MCP node `81:7126` for the post-finished export panel state (`完结/可导出数据`).
- Connected the finished-state `结束实验` action to a right-panel state transition instead of treating the finished run monitor as the export form itself.
- Added the export-data panel in the existing 400px right rail without moving the signal-monitor card: title `导出数据`, Figma summary card rows, required filename field, default-checked EDF+/CSV export options, and the primary `导出数据` CTA.
- Added local source SVG assets derived from the Figma node so the export panel does not depend on a live MCP asset URL:
  - `/assets/icon-export-data-arrow-right-line.svg`
  - `/assets/icon-export-checkbox-check.svg`
- Verification: production Vite build passes; local preview `http://127.0.0.1:5173/` returns HTTP 200; `scripts/verify-point-state.mjs` still passes for all 20 electrodes. Direct in-app click verification was not available because this turn exposed no browser DOM-control tool; no browser-side visual pass is claimed for Iteration 28.

### Iteration 29

- Updated the post-stimulation-impedance top-right next CTA copy from `下一步：实验中` to `下一步：进行实验` based on browser annotation feedback.
- Verification: production Vite build passes after the copy change.

### Iteration 30

- Read Figma MCP node `75:2292` (`自动模式/待机中`) and captured its Figma screenshot for context.
- Implemented the automatic-mode experiment-page differences while preserving the existing manual-mode flow:
  - automatic mode keeps the top-right segmented control active on `自动模式`;
  - the sequence-table third column is `状态`, and the first row remains `待执行` instead of showing an inline `开始` button;
  - added the automatic-mode `刺激循环次数` section with default `1 次`;
  - added the bottom green `开始` action beside the red `紧急停止刺激` action.
- Verification: production Vite build passes; `scripts/verify-point-state.mjs` still passes for all 20 electrodes.

### Iteration 31

- Tightened the electrode-to-experiment gate: `下一步：进行实验` is enabled only when there is a stimulation measurement and every assigned point is measured with the green `优` result state.
- Added a topbar demo helper checkbox `测试点位全通过` beside the next action. When checked, the current assigned points are rendered and validated as green `优` results so demos have a reliable all-pass scenario.
- Kept the demo helper as a derived UI/check state rather than mutating the real point assignments; clicking points, removing tags, or running a real impedance check exits the demo helper state.
- Verification: production Vite build passes; `scripts/verify-point-state.mjs` still passes for all 20 electrodes.

### Iteration 32

- Read the full Figma MCP design context and screenshot for node `59:1831` (`新建实验`), including its child nodes, typography, spacing, colors, and source asset references.
- Moved the primary `下一步：电极配置` action from the bottom action bar into the top-right header position shown in the design, while preserving required subject-ID validation and the transition into electrode configuration.
- Added a direct preview entry at `/?screen=setup` so the updated page can be opened without changing the existing default electrode-review entry.
- Updated the setup shell to the source two-row layout (`64px` header plus flexible content), so the main card fills the exact remaining viewport area with 24px outer spacing at 1440 × 900.
- Matched the active tab, focus state, and primary action to the Figma primary token `#3941b6`; preserved the existing local source icons and 149 × 103 upload illustration that correspond to the Figma assets.
- Verification: production Vite build passes; `scripts/verify-point-state.mjs` still passes for all 20 electrodes.

### Iteration 33

- Reconnected to the local Figma MCP server and read the full design context plus screenshot for node `74:2666` (`历史记录`).
- Added a working `新建实验` / `历史记录` tab switch without changing the existing new-experiment form or its validation flow.
- Implemented the history table from the source layout: 52px header and rows, five equal data columns, fixed 56px action column, source copy for all three records, divider/shadow treatments, and the exact Figma file-record SVG asset at `/assets/icon-history-file-text.svg`.
- Added a direct preview entry at `/?screen=setup&tab=history`; the history state keeps the source header clean by hiding the new-experiment next-step action.
- Verification: production Vite build passes; `scripts/verify-point-state.mjs` still passes for all 20 electrodes; local history preview returns HTTP 200.

### Iteration 34

- Reconnected to the local Figma Desktop MCP server and read the full design context plus screenshot for node `71:2515` (`导入历史数据`).
- Replaced the tolerance-step file input with the designed secondary modal: 500 × 304px frame, 24px spacing, three 52px history rows, fixed action column, source copy, and the exact Figma close/import SVG assets.
- Connected each history-row action to import its `0.03 mA` value into the active tolerance threshold and close the secondary modal; the close button and backdrop also dismiss it without changing the threshold.
- Preserved the parent two-step stimulation workflow, head-model placement, right control panel, assignments, measurement state, and point visibility state.
- Verification: production Vite build passes; `scripts/verify-point-state.mjs` still passes for all 20 electrodes; the local page and both imported Figma SVG assets return HTTP 200. Automated in-app-browser interaction verification could not run because the browser-control runtime raised a `Cannot redefine property: process` bootstrap error; no browser-side screenshot comparison is claimed for this iteration.

### Iteration 35

- Traced the automatic experiment flow and found that `blanking` advanced to the intermediate `stimReady` state, but that state had no automatic transition and incorrectly exposed a second automatic-mode `开始` action.
- Added a regression script at `scripts/verify-auto-mode.mjs`; it first reproduced the stop at `stimReady` and now verifies the complete automatic path `acquisition → blanking → stimReady → stimulation → recovery → finished`.
- Added the missing automatic transition from `stimReady` to `stimulation`, limited that transition to automatic mode so manual mode still pauses before stimulation, and limited the automatic-mode start CTA to standby only.
- Verification: `scripts/verify-auto-mode.mjs` passes; production Vite build passes; `scripts/verify-point-state.mjs` still passes for all 20 electrodes; the experiment preview returns HTTP 200.

### Iteration 36

- Read the full Figma MCP design context and isolated screenshot for node `93:8778`, including both acquisition-active and stimulation-active variants.
- Updated the shared acquisition/stimulation role switch to the source 368px two-column composition: 16px gap, 40px controls, 6px radius, exact padding, primary active fill, 10% primary inactive fill, and the source `0 4px 2px rgba(0, 24, 204, 0.25)` active shadow.
- Added the four original Figma SVG role assets so active and inactive states use their intended white/primary strokes and embedded icon shadows instead of a CSS color filter.
- Kept the existing tab semantics and `activateRole` interaction unchanged; the shared head model, all 20 point coordinates, assignments, impedance results, visibility controls, and stimulation workflow state remain untouched.
- Verification: production Vite build passes; `scripts/verify-point-state.mjs` still passes for all 20 electrodes; `scripts/verify-auto-mode.mjs` still passes for the complete automatic experiment sequence.

### Iteration 37

- Traced the new-experiment entry bug to the setup transition: the initial `pointAssignments` value was empty on a cold load, but returning to setup and entering again only changed `screen`, so the same React instance retained the previous experiment's measured assignments.
- Added a dedicated `createNewExperimentPointAssignments` transition and wired the setup page's validated next action through `startNewExperiment` before entering electrode configuration.
- A new experiment now restores the acquisition role, cathode default, both visibility controls, closed stimulation workflow, and—most importantly—an empty assignment map so all 20 points render with the white/default treatment and no impedance rows or selected tags.
- Added `scripts/verify-new-experiment-state.mjs`; the test reproduced the missing reset before the fix and now verifies both the empty point-state transition and its setup-page integration.
- Verification: new-experiment regression passes; the 20-point state regression passes; automatic-mode phase regression passes; production Vite build passes; the local setup page returns HTTP 200.

### Iteration 38

- Implemented editable acquisition and stimulation durations with a shared `10 ms` to `60 s` range, product-styled `ms` / `s` listboxes, unit-preserving conversion, blur clamping, Enter commit, Escape restore, outside-click close and standby-only editability.
- Replaced the hard-coded acquisition and stimulation transition delays with the configured millisecond values; kept the existing prototype bridge delays for blanking, automatic `stimReady`, and recovery.
- Added a real accumulated run timer that starts with the experiment, updates during manual and automatic flows, freezes on completion or emergency stop, and clears when returning to standby.
- Synchronized the bottom timeline labels with the configured acquisition/stimulation values while retaining the source segment proportions and colors.
- Moved `返回电极配置` into the first position of the experiment status row, matching Figma node `75:2051`, and removed the bottom-left overlay.
- Added `src/experimentTiming.js`, `scripts/verify-experiment-timing.mjs`, the aggregate `pnpm test` script, and `docs/product-interaction-spec.md`.
- Verification: point-state, new-experiment, automatic-mode and experiment-timing scripts pass; production Vite build passes; experiment URL returns HTTP 200. Automated in-app-browser screenshot/console verification could not run because browser-client bootstrap failed with `Cannot redefine property: process`; no visual comparison claim is made for this iteration.

### Iteration 39

- Fixed the manual `刺激待开始` duration-control bug reported from the experiment browser view.
- Root cause: one global `phase === "standby"` editability condition disabled both acquisition and stimulation controls after acquisition began, even though manual stimulation still required a separate start action.
- Added a stage-aware rule: acquisition is editable only on standby; manual stimulation is editable on standby and `stimReady`; automatic-mode stimulation remains locked after the run starts; both roles lock once their own stage is running.
- Added regression assertions for acquisition, manual stimulation, automatic stimulation and running-state lock behavior, and updated the product interaction specification.

### Iteration 40

- Read Figma MCP node `75:2448` for the automatic-mode stimulation cycle input.
- Rebuilt the control to the exact 74 × 32px source structure: 1px `#e2e8f2` border, 6px radius, Inter 14px value field, and a 28px right stepper with `#eef2f9` upper/lower buttons and 16px chevrons.
- Replaced the static display and pointer-disabled buttons with an editable numeric input plus working increment/decrement actions; the supported range is 1–99 and invalid/empty values restore or clamp on blur.
- Connected the value to automatic execution: recovery restarts acquisition while cycles remain, and the experiment reaches `finished` only after the configured number of full cycles.
- Cycle editing is available only while automatic mode is on standby and locks after the run starts.

## Required fidelity surfaces

- Fonts and typography: source font fallbacks, weights, suffix typography, modal title, step labels, summary values and channel row hierarchy are implemented and browser-rendered without wrapping.
- Spacing and layout rhythm: current 1440 × 900 and 2240 × 926 evidence shows the shared head geometry, fixed panel placement, adaptive experiment signal-card width, and stable row alignment; the TI modal matches the 560 × 748 source frame and the HD modal matches the 560 × 618 source frame.
- Colors and visual tokens: primary updated to Figma `#3941b6`; semantic impedance colors retained.
- Image quality and asset fidelity: local source head, neural background, logo, icons, empty-state illustration, tDCS waveform asset, TI waveform asset, HD chevron SVG assets, tolerance completed-step SVG, and experiment signal waveform SVG assets are used; the HD apply-all icon uses the documented lucide fallback because the exact Figma asset export was blocked.
- Copy and content: header, experiment ID, role labels, polarity suffixes, impedance values/statuses, empty state, TI labels/values, HD labels/values and CTA copy are consistent across the tested workflow.
- State validity: semantic impedance colors and impedance rows are point-level. A point shows result color only when that point itself is measured; clicking any point re-sets only that point to the current role's selected/unmeasured state and leaves all other points untouched.

## Interaction and console checks

- Primary interactions tested: role switch to stimulation, stimulation point assignment, polarity switch, stimulation CTA, custom stimulation type dropdown, TI selection, TI modal rendering, HD selection, HD modal rendering, completed stimulation impedance detection, gated tolerance-to-stimulation-impedance flow, enabled next-step transition into experiment, post-result F4 point editing, all 20 point-level measured-click transitions, cross-role point switching, tag deletion, impedance-to-experiment transition, experiment phase advancement, finished export action, emergency-capable run panel, return to electrode configuration, and code-level sequence-control regressions. Browser verification for Iteration 20 was intentionally skipped per user instruction.
- Console errors checked: in-app browser dev logs returned no errors for the TI, HD, F4 post-result edit, point-level cross-role states, or experiment workflow.

## Remaining blocker

None for the current scoped prototype states. No actionable P0/P1/P2 mismatch remains in the reviewed evidence.

final result: passed
