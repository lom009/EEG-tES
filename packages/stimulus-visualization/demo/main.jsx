import React, { useEffect, useRef, useState } from "react";
import { createRoot } from "react-dom/client";
import {
  StimulusVisualization,
  shamACRenderer,
  shamDCRenderer,
  tacsRenderer,
  tdcsRenderer,
  tpcsRenderer,
  trnsRenderer,
} from "../src/index.js";
import "./styles.css";

const LOOP_DURATION_MS = 6000;

function Demo() {
  const [progress, setProgress] = useState(0.065);
  const [isPlaying, setIsPlaying] = useState(false);
  const [currentPolarity, setCurrentPolarity] = useState(1);
  const [stimulusType, setStimulusType] = useState("shamDC");
  const previousFrameRef = useRef(null);
  const renderer = {
    tDCS: tdcsRenderer,
    tACS: tacsRenderer,
    tPCS: tpcsRenderer,
    shamDC: shamDCRenderer,
    shamAC: shamACRenderer,
    tRNS: trnsRenderer,
  }[stimulusType];
  const currentMagnitude = {
    tDCS: 2,
    tACS: 2,
    tPCS: 1.4,
    shamDC: 1.8,
    shamAC: 0.9,
    tRNS: 1.8,
  }[stimulusType];
  const supportsPolarity = !["shamAC", "tRNS"].includes(stimulusType);
  const activeCurrent = supportsPolarity
    ? currentPolarity * currentMagnitude
    : currentMagnitude;

  const chooseStimulusType = (nextType, representativeProgress) => {
    setStimulusType(nextType);
    setProgress(representativeProgress);
    setIsPlaying(false);
  };

  useEffect(() => {
    if (!isPlaying) {
      previousFrameRef.current = null;
      return undefined;
    }

    let animationFrame;
    const update = (timestamp) => {
      const previousFrame = previousFrameRef.current ?? timestamp;
      const elapsed = Math.min(64, timestamp - previousFrame);
      previousFrameRef.current = timestamp;
      setProgress((current) => (current + elapsed / LOOP_DURATION_MS) % 1);
      animationFrame = window.requestAnimationFrame(update);
    };

    animationFrame = window.requestAnimationFrame(update);
    return () => window.cancelAnimationFrame(animationFrame);
  }, [isPlaying]);

  return (
    <main className="demo-shell">
      <section className="demo-panel" aria-labelledby="demo-title">
        <header className="demo-header">
          <div>
            <span>独立组件预览</span>
            <h1 id="demo-title">{renderer.label} 刺激状态动效</h1>
          </div>
          <output>{Math.round(progress * 100)}%</output>
        </header>

        <div className="demo-stage">
          <div className="demo-component-scale">
            <StimulusVisualization
              renderer={renderer}
              progress={progress}
              config={{
                targetCurrent: activeCurrent,
                peakCurrent: activeCurrent,
                pulseCurrent: activeCurrent,
                shamCurrent: activeCurrent,
                shamPeakCurrent: currentMagnitude,
                noiseAmplitude: currentMagnitude,
              }}
            />
          </div>
        </div>

        <div className="demo-controls">
          <label>
            <span>执行进度</span>
            <input
              type="range"
              min="0"
              max="1"
              step="0.001"
              value={progress}
              onChange={(event) => setProgress(Number(event.target.value))}
            />
          </label>
          <div className="demo-actions">
            <button
              type="button"
              className={stimulusType === "tRNS" ? "is-active" : ""}
              onClick={() => chooseStimulusType("tRNS", 0.24)}
            >
              tRNS
            </button>
            <button
              type="button"
              className={stimulusType === "tDCS" ? "is-active" : ""}
              onClick={() => chooseStimulusType("tDCS", 0.5)}
            >
              tDCS
            </button>
            <button
              type="button"
              className={stimulusType === "tACS" ? "is-active" : ""}
              onClick={() => chooseStimulusType("tACS", 0.252)}
            >
              tACS
            </button>
            <button
              type="button"
              className={stimulusType === "tPCS" ? "is-active" : ""}
              onClick={() => chooseStimulusType("tPCS", 0.25)}
            >
              tPCS
            </button>
            <button
              type="button"
              className={stimulusType === "shamDC" ? "is-active" : ""}
              onClick={() => chooseStimulusType("shamDC", 0.065)}
            >
              Sham直流
            </button>
            <button
              type="button"
              className={stimulusType === "shamAC" ? "is-active" : ""}
              onClick={() => chooseStimulusType("shamAC", 0.0375)}
            >
              Sham交流
            </button>
            <button type="button" onClick={() => setIsPlaying((current) => !current)}>
              {isPlaying ? "暂停预览" : "继续预览"}
            </button>
            {supportsPolarity ? (
              <>
                <button
                  type="button"
                  className={currentPolarity > 0 ? "is-active" : ""}
                  onClick={() => setCurrentPolarity(1)}
                >
                  +{currentMagnitude.toFixed(2)} mA
                </button>
                <button
                  type="button"
                  className={currentPolarity < 0 ? "is-active" : ""}
                  onClick={() => setCurrentPolarity(-1)}
                >
                  -{currentMagnitude.toFixed(2)} mA
                </button>
              </>
            ) : (
              <span className="demo-peak-value">
                {stimulusType === "tRNS" ? "幅度" : "峰值"} {currentMagnitude.toFixed(2)} mA
              </span>
            )}
          </div>
        </div>
      </section>
    </main>
  );
}

createRoot(document.getElementById("root")).render(
  <React.StrictMode>
    <Demo />
  </React.StrictMode>,
);
