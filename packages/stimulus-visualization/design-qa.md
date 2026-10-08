# tPCS Design QA

- Source visual truth: `/var/folders/06/0m6g1s_j0939fpz_vd4sdsc00000gn/T/codex-clipboard-730eb4bb-eb70-4ada-ae6f-69f9082f4a71.png`
- Implementation screenshot: `/private/tmp/tpcs-implementation-final.png`
- Focused comparison: `/private/tmp/tpcs-qa-comparison.png`
- Browser viewport: 627 × 1044 CSS px at device scale factor 1
- Source pixels: 1914 × 554
- Implementation pixels: 627 × 1044
- Focused source crop: 1615 × 350, normalized to 1200 px wide
- Focused implementation crop: 280 × 75, normalized to 1200 px wide
- State: positive tPCS, progress 0.25, first pulse active, 1.40 mA

## Full-view comparison evidence

The source is a full parameter-chart screenshot while the implementation is the existing 177 × 104 reusable stimulus card. The chart axes, title and time labels are intentionally outside the component contract. The retained visual truth is the two-pulse waveform, its relative pulse timing, 1.4 mA height ratio and purple treatment.

## Focused region comparison evidence

The combined focused comparison shows the same baseline, two rectangular pulses, approximate 21.6%–32.6% and 70.4%–81.3% pulse windows, and a 1.4/2.0 (70%) visual amplitude. The implementation adds the required moving point and completed/pending path states from the reusable component system.

## Required fidelity surfaces

- Fonts and typography: existing shared component typography is preserved; tPCS label follows the same bold type treatment as tDCS/tACS.
- Spacing and layout rhythm: waveform fits the shared 131 × 38 diagram slot; value and status are separated below the graph.
- Colors and visual tokens: purple stroke, pending-path opacity, white point fill and shared glass card tokens are consistent with the existing renderers.
- Image quality and asset fidelity: the waveform is rendered as resolution-independent functional path geometry; the existing exact point asset is reused.
- Copy and content: `1.40 mA` and `tPCS刺激中` match the selected state and shared status convention.

## Comparison history

1. First pass found a P2 overlap: `1.40 mA` crossed the waveform baseline.
2. The tPCS value and status were moved into the same lower reading area used by tACS.
3. Post-fix capture shows clear separation, a centered point at `(33.25, 11.8)`, and no browser warnings or errors.

## Findings

No actionable P0, P1 or P2 differences remain within the compact-component scope.

## Open questions

- The source does not specify exact pulse times beyond the visible chart, so the measured normalized windows remain configurable through `pulseWindows`.

## Implementation checklist

- Keep the screenshot-derived defaults.
- Let product configuration override `pulseCurrent` and `pulseWindows`.
- Preserve the shared card dimensions when integrating other stimulation types.

## Follow-up polish

- Exact clinical protocol timing can replace the screenshot-derived defaults when that specification is available.

final result: passed

---

# tRNS Simplification Design QA

- Source visual truth: `/var/folders/06/0m6g1s_j0939fpz_vd4sdsc00000gn/T/codex-clipboard-7ba82304-5021-45b7-a067-788774b8c8f5.png`
- Implementation screenshot: `/private/tmp/trns-simplified-preview.png`
- Focused combined comparison: `/private/tmp/trns-design-qa-comparison.png`
- Browser viewport: 1280 × 720 CSS px at device scale factor 1
- Source pixels: 1640 × 876
- Implementation pixels: 1280 × 720
- State: tRNS, progress 0.24, paused, configured amplitude 1.80 mA

## Full-view comparison evidence

The source is a full stimulation-plan chart while the implementation remains the existing 177 × 104 reusable status component. Axes, protocol controls and the dense sampled trace are intentionally outside the compact-component contract. The implementation retains the source's zero-centered, non-periodic purple noise-wave character and the shared component styling.

## Focused region comparison evidence

The combined comparison shows the deliberate simplification from dozens of source turns to nine control points with seven interior turns. The path still reads as irregular biphasic noise rather than a sine, square or triangular protocol. The moving point is centered at `(31.96, 7.8)` on the selected vertex.

## Required fidelity surfaces

- Fonts and typography: existing shared component typography and the `tRNS刺激中` status convention are preserved.
- Spacing and layout rhythm: the trace fits the shared 131 × 38 graph slot without colliding with the value.
- Colors and visual tokens: purple stroke, pending opacity, completed path and white-centered point reuse the established tokens.
- Image quality and asset fidelity: the functional waveform remains resolution-independent and reuses the exact existing point asset; no decorative source asset is required.
- Copy and content: `1.80 mA` is shown as the static configured amplitude; the demo labels it `幅度 1.80 mA` and exposes no polarity controls.

## Interaction checks

- Playback advanced progress from `0.240` to `0.351` and moved the point from `(31.96, 7.8)` to `(46.2747, 20.091692)`.
- The displayed value stayed at `1.80 mA` throughout playback.
- No positive or negative polarity button is rendered for tRNS.
- Browser console reported no errors or warnings.

## Findings

No actionable P0, P1 or P2 differences remain within the explicitly simplified compact-component scope.

## Comparison history

1. The user identified the source waveform's many turns as too complex for the status component.
2. The implementation reduced it to seven interior turns while preserving the random-noise visual category.
3. The combined post-change comparison confirms the reduced density and unchanged shared component hierarchy.

## Follow-up polish

- The fixed control points can be replaced with a product-approved simplified trace later without changing the renderer interface.

final result: passed

---

# Sham DC / AC Design QA

- Sham DC source: `/var/folders/06/0m6g1s_j0939fpz_vd4sdsc00000gn/T/codex-clipboard-fd0ac8d9-d1c8-4110-8d86-4bed04f9a4ac.png`
- Sham AC source: `/var/folders/06/0m6g1s_j0939fpz_vd4sdsc00000gn/T/codex-clipboard-5a1ffd9a-c15c-4d9c-b965-ca190f32ed69.png`
- Positive Sham DC implementation: `/private/tmp/sham-dc-positive.png`
- Negative Sham DC implementation: `/private/tmp/sham-dc-negative.png`
- Sham AC implementation: `/private/tmp/sham-ac.png`
- Combined Sham DC comparison: `/private/tmp/sham-dc-design-qa.png`
- Combined Sham AC comparison: `/private/tmp/sham-ac-design-qa.png`
- Browser viewport: 805 × 1044 CSS px at device scale factor 1

## Comparison evidence

The combined inputs show the source and compact implementation together. Sham DC preserves two narrow triangular pulses at the beginning and end, with the long zero-current baseline between them. The negative state is the intentional vertical mirror requested by the user. Sham AC preserves one positive/negative cycle at each end and a centered zero-current baseline through the middle.

The original screenshots include full chart axes and time labels; those remain outside this standalone 177 × 104 status component. Relative timing, amplitude, waveform direction, purple treatment, moving point and synchronized instantaneous value are retained.

## Interaction and state checks

- Positive Sham DC at progress `0.065`: point center `(9.385, 4.6)`, value `1.80 mA`.
- Negative Sham DC at progress `0.065`: point center `(9.385, 33.4)`, value `-1.80 mA`; both triangles and the baseline are mirrored.
- Sham AC at progress `0.0375`: point center `(4.8375, 1)`, instantaneous value `0.90 mA`.
- Sham AC exposes a single `0.90 mA` peak-amplitude badge and no positive/negative polarity controls.
- The moving point is centered on the rendered path in all three checked states.

## Findings

No actionable P0, P1 or P2 differences remain within the compact-component scope. The exact clinical pulse and burst windows remain configurable because the screenshots communicate relative timing rather than protocol values.

final result: passed

---

# Sham DC Peak Guide Design QA

- Source visual truth: `/var/folders/06/0m6g1s_j0939fpz_vd4sdsc00000gn/T/codex-clipboard-5382963e-df9a-461f-8aec-42cc714b41be.png`
- Implementation screenshot: `/private/tmp/sham-dc-peak-guides-final.png`
- Focused implementation crop: `/private/tmp/sham-dc-peak-guides-focused.png`
- Combined comparison: `/private/tmp/sham-dc-guides-design-qa.png`
- Browser viewport: 1024 × 1048 CSS px at device scale factor 1
- Source pixels: 860 × 354
- Implementation pixels: 1024 × 1048
- State: positive Sham DC, progress 0.065, first peak active, 1.80 mA

## Comparison evidence

The combined comparison preserves the supplied Sham DC waveform, card hierarchy, value and status. The requested change adds exactly two vertical dashed guides, each centered on one triangular peak at x positions `9.385` and `123.034` in the 131 × 38 graph coordinate system.

## Required fidelity surfaces

- Fonts and typography: unchanged from the supplied Sham DC state.
- Spacing and layout rhythm: both guides remain inside the existing graph slot and do not change component dimensions.
- Colors and visual tokens: guides reuse the exact tDCS `tdcs-guide.svg` asset with its 50% purple opacity, 0.5 px stroke and `1 1` dash pattern.
- Image quality and asset fidelity: the existing source asset is reused rather than approximating the dash treatment.
- Copy and content: `1.80 mA` and `Sham直流刺激中` remain unchanged.

## Interaction checks

- Positive state: first guide and point share x `9.385`; second guide is centered at x `123.034`.
- Negative state: guides remain at the same peak x positions while the waveform and point mirror vertically; value becomes `-1.80 mA`.
- Custom triangle windows derive guide positions from their configured peak coordinates.
- Browser console reported no errors or warnings.

## Findings

No actionable P0, P1 or P2 differences remain. The only visible deviation from the supplied screenshot is the explicitly requested pair of tDCS-style peak guides.

final result: passed
