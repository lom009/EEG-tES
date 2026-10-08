import { useEffect, useRef } from "react";
import "./TiltedSurface.css";

export function TiltedSurface({ children, className = "", maxTilt = 3 }) {
  const surfaceRef = useRef(null);
  const frameRef = useRef(0);

  useEffect(() => () => window.cancelAnimationFrame(frameRef.current), []);

  function handlePointerMove(event) {
    if (event.pointerType === "touch" || !surfaceRef.current) return;
    const rect = event.currentTarget.getBoundingClientRect();
    const normalizedX = (event.clientX - rect.left) / rect.width - 0.5;
    const normalizedY = (event.clientY - rect.top) / rect.height - 0.5;
    const rotateX = normalizedY * maxTilt * -2;
    const rotateY = normalizedX * maxTilt * 2;

    window.cancelAnimationFrame(frameRef.current);
    frameRef.current = window.requestAnimationFrame(() => {
      surfaceRef.current?.style.setProperty("--tilt-x", `${rotateX.toFixed(2)}deg`);
      surfaceRef.current?.style.setProperty("--tilt-y", `${rotateY.toFixed(2)}deg`);
      surfaceRef.current?.style.setProperty("--tilt-scale", "1.008");
    });
  }

  function resetTilt() {
    window.cancelAnimationFrame(frameRef.current);
    surfaceRef.current?.style.setProperty("--tilt-x", "0deg");
    surfaceRef.current?.style.setProperty("--tilt-y", "0deg");
    surfaceRef.current?.style.setProperty("--tilt-scale", "1");
  }

  return (
    <div
      className={`tilted-surface ${className}`.trim()}
      onPointerMove={handlePointerMove}
      onPointerLeave={resetTilt}
    >
      <div className="tilted-surface__inner" ref={surfaceRef}>
        {children}
      </div>
    </div>
  );
}
