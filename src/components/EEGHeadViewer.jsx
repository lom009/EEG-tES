import { useEffect, useRef, useState } from 'react';
import { createHeadScene } from '../head-test/createHeadScene.js';
import './EEGHeadViewer.css';
export function EEGHeadViewer(props) {
  const host = useRef(null), runtime = useRef(null), latest = useRef(props);
  latest.current = props;
  const [loading, setLoading] = useState(true), [error, setError] = useState('');
  useEffect(() => {
    let active = true;
    try {
      runtime.current = createHeadScene(host.current, () => latest.current,
        () => { if (active) setLoading(false); },
        () => { if (active) { setLoading(false); setError('模型加载失败，请刷新重试。'); } });
    } catch { setLoading(false); setError('无法启动三维视图，请确认浏览器已启用硬件加速。'); }
    return () => { active = false; runtime.current?.dispose(); runtime.current = null; };
  }, []);
  return <div className="eeg-viewer" data-ready={!loading && !error}>
    <div className="eeg-canvas" ref={host} />
    {loading && <div className="eeg-loading" role="status">正在加载三维头模…</div>}
    {error && <div className="eeg-loading" role="alert">{error}</div>}
    <div className="eeg-view-controls"><button type="button" onClick={() => runtime.current?.reset()}>重置视角</button></div>
    <div className="eeg-view-hint">拖动旋转 · 滚轮缩放 · 点击电极选择</div>
  </div>;
}
