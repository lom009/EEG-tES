import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { RoomEnvironment } from 'three/addons/environments/RoomEnvironment.js';
import {
  advanceDetectionSession,
  beginDetectionSession,
  ELECTRODE_COLORS,
  getElectrodeVisualState,
  placeElectrodeLabel,
  shouldUpdateLabels,
  stopDetectionSession,
} from './electrode-visuals.js';
import { getPointDisplayLabel, getPointVisualState } from '../pointState.js';
import { applyNeckFade } from './neck-fade.js';

export function createHeadScene(stage, getState, onReady, onError) {
let disposed = false;
const status = { textContent: '' };
const checkButton = document.createElement('button');
const clearButton = document.createElement('button');
const resetButton = document.createElement('button');
let innerWidth = stage.clientWidth, innerHeight = stage.clientHeight;
const renderer = new THREE.WebGLRenderer({
  antialias: true,
  alpha: true,
  powerPreference: 'high-performance',
});
renderer.setPixelRatio(Math.min(devicePixelRatio, 1.5));
renderer.outputColorSpace = THREE.SRGBColorSpace;
renderer.toneMapping = THREE.ACESFilmicToneMapping;
renderer.toneMappingExposure = 0.88;
renderer.setClearColor(0x000000, 0);
renderer.shadowMap.enabled = false;
stage.append(renderer.domElement);

const labels = document.createElement('div');
labels.className = 'eeg-test-labels';
stage.append(labels);

const scene = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(27, 1, 0.001, 10);
const controls = new OrbitControls(camera, renderer.domElement);
controls.enableDamping = true;
controls.enablePan = false;
controls.dampingFactor = 0.07;
controls.rotateSpeed = 0.62;
controls.zoomSpeed = 0.78;
controls.minPolarAngle = 0.22;
controls.maxPolarAngle = Math.PI / 2;

const pmrem = new THREE.PMREMGenerator(renderer);
const room = new RoomEnvironment();
const env = pmrem.fromScene(room, 0.04);
scene.environment = env.texture;
scene.environmentIntensity = 0.3;
room.dispose();
pmrem.dispose();

scene.add(new THREE.HemisphereLight('#f3f8ff', '#8da8c6', 0.48));

const key = new THREE.DirectionalLight('#eef5ff', 1.55);
key.position.set(-0.35, 0.62, 0.46);
key.target.position.set(0, 0.245, 0);
scene.add(key, key.target);

// Soft neutral fill keeps the white head readable on the unlit side.
const fill = new THREE.DirectionalLight('#f3f8ff', 0.65);
fill.position.set(0.6, 0.3, 0.8);
fill.target.position.set(0, 0.245, 0);
scene.add(fill, fill.target);

const rim = new THREE.DirectionalLight('#cfe2ff', 0.68);
rim.position.set(0.7, 0.68, -1.2);
rim.target.position.set(0, 0.25, 0);
scene.add(rim, rim.target);

const selected = new Set();
const checking = new Set();
const results = new Map();
const entries = [];
const electrodeMeshes = [];
const ray = new THREE.Raycaster();
const ndc = new THREE.Vector2();
const upAxis = new THREE.Vector3(0, 0, 1);
const selectedBlue = new THREE.Color(ELECTRODE_COLORS.selected);
let down = null;
let labelsDirty = true;
let controlsActive = false;
let detectionSession = null;
let nextDetectionUpdateAt = 0;

function syncSummary() {
  status.textContent = selected.size
    ? `已选 ${selected.size} 个：${[...selected].join('、')}`
    : '点击点位开始选择';
  checkButton.disabled = selected.size === 0;
  checkButton.textContent = checking.size ? '停止检测' : '开始检测';
  checkButton.classList.toggle('is-detecting', checking.size > 0);
  checkButton.setAttribute('aria-pressed', String(checking.size > 0));
  clearButton.disabled = selected.size === 0 || checking.size > 0;
}

function paintEntry(entry) {
  const appearance = getElectrodeVisualState({
    assigned: selected.has(entry.id),
    checking: checking.has(entry.id),
    result: results.get(entry.id) ?? null,
  });
  const color = new THREE.Color(appearance.color);
  entry.button.setAttribute('aria-pressed', String(selected.has(entry.id)));
  entry.button.dataset.state = appearance.key;
  entry.button.style.setProperty('--point-state', appearance.color);
  const isChecking = checking.has(entry.id);
  entry.effect.holder.visible = isChecking;
  for (const particle of entry.effect.particles) particle.visible = isChecking;

  if (!entry.transition) {
    for (const highlight of entry.highlight) highlight.material.color.copy(color);
  }
  for (const highlight of entry.highlight) {
    if (!('emissive' in highlight.material)) continue;
    if (appearance.key === 'selected' || appearance.key === 'checking') {
      highlight.material.emissive.copy(appearance.key === 'checking' ? color : selectedBlue);
      highlight.material.emissiveIntensity = appearance.key === 'checking' ? 0.3 : 0.16;
    } else if (appearance.key === 'default') {
      highlight.material.emissive.set('#000000');
      highlight.material.emissiveIntensity = 0;
    } else {
      highlight.material.emissive.copy(color);
      highlight.material.emissiveIntensity = 0.05;
    }
  }
}

function paintAllEntries() {
  for (const entry of entries) paintEntry(entry);
  syncSummary();
  labelsDirty = true;
}

function select(id) {
  if (!getState().checking) getState().onPointClick(id);
}

function reset() {
  const isWide = camera.aspect > 1.45;
  const target = new THREE.Vector3(0, isWide ? 0.205 : 0.218, 0.003);
  const scale = isWide ? 1.2 : 1;
  controls.target.copy(target);
  camera.position.copy(target).add(new THREE.Vector3(-0.205, 0.112, 0.39).multiplyScalar(scale));
  controls.minDistance = 0.29;
  controls.maxDistance = 0.92;
  controls.update();
  labelsDirty = true;
}

function clearSelection() {
  if (checking.size) return;
  selected.clear();
  results.clear();
  for (const entry of entries) entry.transition = null;
  paintAllEntries();
}

function createCheckingEffect(entry, hub) {
  const holder = new THREE.Group();
  holder.position.copy(entry.position).addScaledVector(entry.normal, 0.0025);
  holder.quaternion.setFromUnitVectors(upAxis, entry.normal);
  holder.visible = false;

  const arc = new THREE.Mesh(
    new THREE.RingGeometry(0.00465, 0.00545, 56, 1, 0, Math.PI * 0.62),
    new THREE.MeshBasicMaterial({
      color: ELECTRODE_COLORS.selected,
      transparent: true,
      opacity: 0.88,
      side: THREE.DoubleSide,
      depthWrite: false,
      blending: THREE.AdditiveBlending,
    }),
  );
  holder.add(arc);
  scene.add(holder);

  const particles = [];
  let curve = null;
  if (entry.id !== hub.id) {
    const start = entry.position.clone().addScaledVector(entry.normal, 0.0032);
    const end = hub.position.clone().addScaledVector(hub.normal, 0.0034);
    const curvePoints = [];
    for (let index = 0; index <= 4; index += 1) {
      const t = index / 4;
      const liftNormal = entry.normal.clone().lerp(hub.normal, t).normalize();
      curvePoints.push(
        start.clone().lerp(end, t).addScaledVector(liftNormal, Math.sin(Math.PI * t) * 0.005),
      );
    }
    curve = new THREE.CatmullRomCurve3(curvePoints);
    for (const opacity of [0.88, 0.36]) {
      const particle = new THREE.Mesh(
        new THREE.SphereGeometry(0.00105, 10, 8),
        new THREE.MeshBasicMaterial({
          color: '#72a8ff',
          transparent: true,
          opacity,
          depthWrite: false,
          blending: THREE.AdditiveBlending,
        }),
      );
      particle.visible = false;
      scene.add(particle);
      particles.push(particle);
    }
  }

  return { holder, arc, particles, curve };
}

function applyLiveDetectionResults(now) {
  if (!detectionSession) return;
  for (const id of detectionSession.pointIds) {
    const result = detectionSession.results[id];
    const entry = entries.find((item) => item.id === id);
    if (!entry || !result) continue;
    const from = entry.highlight[0]?.material.color.clone()
      ?? new THREE.Color(ELECTRODE_COLORS.selected);
    results.set(id, result);
    entry.transition = {
      from,
      to: new THREE.Color(ELECTRODE_COLORS[result]),
      startedAt: now,
      duration: 420,
    };
  }
  paintAllEntries();
}

function startDetection(now = performance.now()) {
  if (!selected.size || checking.size) return;
  detectionSession = beginDetectionSession(selected);
  for (const id of detectionSession.pointIds) checking.add(id);
  applyLiveDetectionResults(now);
  nextDetectionUpdateAt = now + 1100;
}

function stopDetection() {
  if (!detectionSession?.running) return;
  detectionSession = stopDetectionSession(detectionSession);
  checking.clear();
  nextDetectionUpdateAt = 0;
  for (const id of detectionSession.pointIds) {
    const entry = entries.find((item) => item.id === id);
    if (entry) entry.transition = null;
  }
  paintAllEntries();
}

function toggleDetection() {
  if (checking.size) stopDetection();
  else startDetection();
}

function animateEffects(now) {
  const seconds = now / 1000;
  if (detectionSession?.running && now >= nextDetectionUpdateAt) {
    detectionSession = advanceDetectionSession(detectionSession);
    applyLiveDetectionResults(now);
    nextDetectionUpdateAt = now + 1100;
  }
  for (const entry of entries) {
    if (checking.has(entry.id)) {
      entry.effect.arc.rotation.z = seconds * 4.5;
      const pulse = 0.5 + 0.5 * Math.sin(seconds * Math.PI * 3.1 + entry.index * 0.45);
      entry.effect.holder.scale.setScalar(1 + pulse * 0.055);
      for (const highlight of entry.highlight) {
        if ('emissiveIntensity' in highlight.material) {
          highlight.material.emissiveIntensity = 0.2 + pulse * 0.28;
        }
      }
      entry.effect.particles.forEach((particle, particleIndex) => {
        if (!entry.effect.curve) return;
        const phase = (seconds * 0.62 + entry.index * 0.08 - particleIndex * 0.2 + 10) % 1;
        particle.position.copy(entry.effect.curve.getPoint(phase));
        const fade = Math.sin(Math.PI * phase);
        particle.scale.setScalar(0.72 + fade * 0.42);
      });
    }

    if (entry.transition) {
      const elapsed = (now - entry.transition.startedAt) / entry.transition.duration;
      const t = Math.min(1, Math.max(0, elapsed));
      const eased = 1 - (1 - t) ** 3;
      for (const highlight of entry.highlight) {
        highlight.material.color.lerpColors(entry.transition.from, entry.transition.to, eased);
      }
      if (t >= 1) entry.transition = null;
    }
  }
}

resetButton.onclick = reset;
clearButton.onclick = clearSelection;
checkButton.onclick = toggleDetection;

const resize = () => {
  renderer.setSize(innerWidth, innerHeight);
  camera.aspect = innerWidth / innerHeight;
  camera.updateProjectionMatrix();
  labelsDirty = true;
};
const observer = new ResizeObserver(() => { innerWidth = stage.clientWidth; innerHeight = stage.clientHeight; resize(); });
observer.observe(stage);
resize();
reset();

let model;
(async () => { try {
  const [gltf, data] = await Promise.all([
    new GLTFLoader().loadAsync('/assets/eeg/eeg-head.glb?test=quality50-25520368'),
    fetch('/assets/eeg/points.json?test=quality50-25520368', { cache: 'no-store' }).then((response) => {
      if (!response.ok) throw Error('点位加载失败');
      return response.json();
    }),
  ]);
  if (disposed) { gltf.scene.traverse(o => { o.geometry?.dispose(); o.material?.dispose(); }); return; }
  model = gltf.scene;
  scene.add(gltf.scene);
  gltf.scene.updateMatrixWorld(true);
  labelsDirty = true;

  const fadedHeads = [];
  gltf.scene.traverse((object) => {
    if (!object.isMesh) return;
    object.castShadow = object.name === 'HEAD' || object.name === 'CAP' || /Strap|Binding/.test(object.name);
    object.receiveShadow = object.name === 'HEAD' || object.name === 'CAP';
    const source = object.material;
    const material = object.name === 'HEAD'
      ? new THREE.MeshPhysicalMaterial({
        name: source.name,
        color: '#f4f7fa',
        roughness: 0.48,
        metalness: 0,
        transmission: 0,
        transparent: true,
        opacity: 1,
        thickness: 0.008,
        ior: 1.35,
        clearcoat: 0.05,
        clearcoatRoughness: 0.7,
        side: THREE.FrontSide,
      })
      : object.name === 'CAP'
      ? new THREE.MeshStandardMaterial({
        name: source.name,
        map: source.map,
        normalMap: source.normalMap,
        normalScale: source.normalScale?.clone(),
        roughnessMap: source.roughnessMap,
        metalnessMap: source.metalnessMap,
        aoMap: source.aoMap,
        alphaMap: source.alphaMap,
        side: source.side,
      })
      : source.clone();
    object.material = material;
    if (object.name === 'HEAD' || /eye|cornea/i.test(object.name)) {
      if (object.name !== 'HEAD') {
        material.color.set('#f7faff'); material.roughness = 0.52; material.metalness = 0;
      }
      material.envMapIntensity = 0.3; material.aoMapIntensity = 0.05;
      if (object.name === 'HEAD') fadedHeads.push([object.parent, applyNeckFade(object)]);
    } else if (object.name === 'CAP') {
      material.color.set('#bfd0e2'); material.roughness = 0.75; material.metalness = 0;
      material.envMapIntensity = 0.2; material.aoMapIntensity = 0.12;
      material.transparent = true; material.opacity = 0.64;
      if (material.normalMap) material.normalScale.set(0.4, 0.4);
    } else if (/CAP_Cables$/.test(object.name)) {
      material.color.set('#9ab7d3'); material.roughness = 0.62; material.metalness = 0;
      material.envMapIntensity = 0.24;
    } else if (/Electrode_Base/.test(object.name)) {
      material.color.set(ELECTRODE_COLORS.default); material.roughness = 0.34;
      material.metalness = 0.01; material.envMapIntensity = 0.38;
    } else if (/Electrode_White|Electrode_Face/.test(object.name)) {
      material.color.set(ELECTRODE_COLORS.default); material.roughness = 0.36;
      material.metalness = 0; material.envMapIntensity = 0.36;
    } else if (/Buckle|Dial|Clip|Adjuster|Electrode|Cable_Anchor/.test(object.name)) {
      material.color.set('#dce8f4'); material.roughness = 0.36;
      material.metalness = 0.02; material.envMapIntensity = 0.38;
    } else if (/Strap|Binding|Seam|Stitch/.test(object.name)) {
      material.color.set('#b8cce3'); material.roughness = 0.56;
      material.metalness = 0; material.envMapIntensity = 0.24;
    }
  });
  for (const [parent, colorMesh] of fadedHeads) parent.add(colorMesh);

  for (const [index, [id, point]] of Object.entries(data.points).entries()) {
    const group = gltf.scene.getObjectByName(`E_${id}`);
    if (!group) throw Error(`模型缺少点位 ${id}`);
    const button = document.createElement('button');
    button.className = 'eeg-test-point';
    button.dataset.point = id;
    button.textContent = id;
    button.setAttribute('aria-pressed', 'false');
    button.onclick = () => select(id);
    labels.append(button);

    const highlight = [];
    const anchor = new THREE.Vector3(...point.position);
    group.traverse((object) => {
      if (!object.isMesh) return;
      electrodeMeshes.push(object);
      if (/Electrode_Face/.test(object.name)) object.getWorldPosition(anchor);
      if (/Base|Face/.test(object.name)) {
        object.material = object.material.clone();
        highlight.push({ material: object.material });
      }
    });
    entries.push({
      id,
      index,
      group,
      button,
      position: anchor,
      normal: new THREE.Vector3(...point.normal).normalize(),
      highlight,
      transition: null,
      effect: null,
    });
  }

  const hub = entries.find((entry) => entry.id === 'Cz') ?? entries[0];
  for (const entry of entries) {
    entry.effect = createCheckingEffect(entry, hub);
    paintEntry(entry);
  }
  syncSummary();
  onReady();
  lastState = null;
} catch (error) {
  status.textContent = `加载失败：${error.message}`;
  if (!disposed) onError(error);
} })();

renderer.domElement.addEventListener('pointerdown', (event) => {
  down = [event.clientX, event.clientY];
});
renderer.domElement.addEventListener('pointerup', (event) => {
  if (!down || Math.hypot(event.clientX - down[0], event.clientY - down[1]) > 4) {
    down = null;
    return;
  }
  down = null;
  const rect = renderer.domElement.getBoundingClientRect();
  ndc.set((event.clientX - rect.left) / rect.width * 2 - 1, 1 - (event.clientY - rect.top) / rect.height * 2);
  ray.setFromCamera(ndc, camera);
  let object = model ? ray.intersectObject(model, true)[0]?.object : null;
  while (object && !/^E_(FP1|FP2|F7|F3|Fz|F4|F8|T3|C3|Cz|C4|T4|T5|P3|Pz|P4|T6|O1|Oz|O2)$/.test(object.name)) {
    object = object.parent;
  }
  if (object) select(object.name.slice(2));
});

const projected = new THREE.Vector3();
const direction = new THREE.Vector3();
const projectedCenter = new THREE.Vector3();
controls.addEventListener('start', () => {
  controlsActive = true;
  labelsDirty = true;
});
controls.addEventListener('change', () => { labelsDirty = true; });
controls.addEventListener('end', () => {
  controlsActive = false;
  labelsDirty = true;
});

function updateLabels() {
  camera.updateMatrixWorld();
  projectedCenter.copy(controls.target).project(camera);
  const stageCenter = {
    x: (projectedCenter.x + 1) * innerWidth / 2,
    y: (1 - projectedCenter.y) * innerHeight / 2,
  };
  const candidates = [];
  for (const entry of entries) {
    direction.copy(camera.position).sub(entry.position).normalize();
    projected.copy(entry.position).project(camera);
    const facingCamera = entry.normal.dot(direction) > 0.16;
    const insideFrustum = projected.z > -1 && projected.z < 1
      && Math.abs(projected.x) < 0.98 && Math.abs(projected.y) < 0.96;
    entry.button.style.visibility = 'hidden';
    entry.button.tabIndex = -1;
    if (!facingCamera || !insideFrustum) continue;
    candidates.push({
      entry,
      anchor: {
        x: (projected.x + 1) * innerWidth / 2,
        y: (1 - projected.y) * innerHeight / 2,
      },
      z: projected.z,
      active: selected.has(entry.id),
    });
  }

  candidates.sort((a, b) => Number(b.active) - Number(a.active) || a.z - b.z);
  const occupied = [];
  for (const candidate of candidates) {
    const placement = placeElectrodeLabel({
      anchor: candidate.anchor,
      stageCenter,
      viewport: { width: innerWidth, height: innerHeight },
      occupied,
      labelWidth: candidate.entry.button.offsetWidth || 44,
      labelHeight: 34,
    });
    if (!placement) continue;
    const { entry } = candidate;
    entry.button.style.visibility = 'visible';
    entry.button.tabIndex = 0;
    entry.button.style.transform = `translate3d(${placement.x}px, ${placement.y}px, 0) translate(-50%, -50%)`;
    entry.button.style.zIndex = String(Math.round((1 - candidate.z) * 1000));
    occupied.push(placement);
  }
}

let lastState = null;
renderer.setAnimationLoop((now) => {
  const moving = controls.update();
  const state = getState();
  if (state !== lastState) {
    lastState = state;
    selected.clear(); checking.clear();
    for (const entry of entries) {
      const assignment = state.assignments[entry.id];
      if (assignment) selected.add(entry.id);
      const isChecking = state.checking && assignment?.role === state.checkingRole;
      if (isChecking) checking.add(entry.id);
      const visual = getPointVisualState(isChecking ? { ...assignment, measured: false } : assignment, { hasError: state.distanceConflict && entry.id === 'P4' });
      const palette = { default: '#f8fbff', selected: '#286cff', 'stim-a': '#286cff', 'stim-c': '#286cff', excellent: '#32a477', good: '#20b8cc', medium: '#f4ac26', poor: '#f17542', bad: '#e3444f', error: '#e3444f' };
      const color = palette[visual] || palette.selected;
      const muted = assignment && (assignment.role === 'acquisition' ? !state.showAcquisition : !state.showStimulation);
      entry.button.textContent = getPointDisplayLabel(entry.id, assignment);
      entry.button.setAttribute('aria-label', entry.button.textContent);
      entry.button.setAttribute('aria-pressed', String(!!assignment));
      entry.button.dataset.state = isChecking ? 'checking' : visual;
      entry.button.style.setProperty('--point-state', color);
      entry.button.style.opacity = muted ? '0.3' : '1';
      for (const mesh of entry.highlight) {
        mesh.material.color.set(color);
        mesh.material.emissive?.set(assignment ? color : '#000000');
        mesh.material.emissiveIntensity = assignment ? 0.16 : 0;
      }
      entry.group.traverse(o => { if (o.isMesh) { o.material.transparent = !!muted; o.material.opacity = muted ? 0.3 : 1; o.material.depthWrite = !muted; } });
      entry.effect.holder.visible = !!isChecking;
      entry.effect.particles.forEach(p => { p.visible = !!isChecking; });
    }
    labelsDirty = true;
  }
  animateEffects(now);
  if (shouldUpdateLabels({ controlsActive, controlsChanged: moving, labelsDirty })) {
    updateLabels();
    labelsDirty = false;
  }
  renderer.render(scene, camera);
});

return { reset, dispose() {
  disposed = true;
  observer.disconnect(); renderer.setAnimationLoop(null); controls.dispose();
  const geometries = new Set(), materials = new Set(), textures = new Set();
  scene.traverse(o => { if (o.geometry) geometries.add(o.geometry); for (const m of (Array.isArray(o.material) ? o.material : [o.material])) if (m) { materials.add(m); Object.values(m).forEach(v => { if (v?.isTexture) textures.add(v); }); } });
  geometries.forEach(g => g.dispose()); materials.forEach(m => m.dispose()); textures.forEach(t => t.dispose()); env.dispose(); renderer.dispose(); renderer.domElement.remove(); labels.remove();
} };
}
