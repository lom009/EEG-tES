import { useState } from "react";
import "./ImportFolder.css";

export function ImportFolder({ hasFile = false, onActivate }) {
  const [isOpen, setIsOpen] = useState(false);
  const open = isOpen || hasFile;

  return (
    <button
      type="button"
      className={`import-folder${open ? " is-open" : ""}${hasFile ? " has-file" : ""}`}
      aria-label={hasFile ? "重新选择实验参数包" : "选择实验参数包"}
      onClick={() => {
        setIsOpen(true);
        onActivate?.();
      }}
      onMouseEnter={() => setIsOpen(true)}
      onMouseLeave={() => setIsOpen(false)}
      onFocus={() => setIsOpen(true)}
      onBlur={() => setIsOpen(false)}
    >
      <span className="import-folder__body" aria-hidden="true">
        <span className="import-folder__back" />
        <span className="import-folder__paper import-folder__paper--one" />
        <span className="import-folder__paper import-folder__paper--two" />
        <span className="import-folder__paper import-folder__paper--three">
          <span className="import-folder__paper-line" />
          <span className="import-folder__paper-line import-folder__paper-line--short" />
        </span>
        <span className="import-folder__front" />
        <span className="import-folder__tab" />
        {hasFile ? <span className="import-folder__status">EXPP</span> : null}
      </span>
    </button>
  );
}
