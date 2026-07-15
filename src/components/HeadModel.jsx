import { useEffect, useRef, useState } from "react";
import * as THREE from "three";
import { OrbitControls } from "three/examples/jsm/controls/OrbitControls.js";
import { GLTFLoader } from "three/examples/jsm/loaders/GLTFLoader.js";

const ELECTRODES = [
  ["FP1", -0.22, -0.7], ["FP2", 0.22, -0.7],
  ["F7", -0.62, -0.38], ["F3", -0.3, -0.45], ["Fz", 0, -0.52], ["F4", 0.3, -0.45], ["F8", 0.62, -0.38],
  ["T3", -0.72, 0], ["C3", -0.34, 0], ["Cz", 0, 0], ["C4", 0.34, 0], ["T4", 0.72, 0],
  ["T5", -0.58, 0.35], ["P3", -0.3, 0.43], ["Pz", 0, 0.48], ["P4", 0.3, 0.43], ["T6", 0.58, 0.35],
  ["O1", -0.23, 0.68], ["Oz", 0, 0.73], ["O2", 0.23, 0.68],
];

const INITIAL_STATES = { F3: "poor", F4: "bad", Cz: "medium", P3: "excellent", T6: "excellent" };
const POINT_STYLES = {
  default: { fill: null, shadow: "rgba(104,95,183,.25)", text: "#364459" },
  excellent: { fill: "#38a169", shadow: "rgba(95,183,142,.25)", text: "#ffffff" },
  good: { fill: "#00b5da", shadow: "rgba(95,120,183,.25)", text: "#ffffff" },
  medium: { fill: "#f3a619", shadow: "rgba(183,161,95,.25)", text: "#ffffff" },
  poor: { fill: "#fd5b38", shadow: "rgba(183,116,95,.25)", text: "#ffffff" },
  bad: { fill: "#e91919", shadow: "rgba(183,95,97,.25)", text: "#ffffff" },
};

function makeElectrodeTexture(label, state = "default", role = "acquisition", polarity = "A") {
  const canvas = document.createElement("canvas");
  canvas.width = 200;
  canvas.height = 200;
  const context = canvas.getContext("2d");
  const style = POINT_STYLES[state] || POINT_STYLES.default;
  const centerX = 100;
  const centerY = 92;
  const radius = 80;
  context.clearRect(0, 0, 200, 200);
  context.shadowColor = style.shadow;
  context.shadowBlur = 8;
  context.shadowOffsetY = 16;
  context.beginPath();
  context.arc(centerX, centerY, radius, 0, Math.PI * 2);
  if (style.fill) {
    context.fillStyle = style.fill;
  } else {
    const gradient = context.createLinearGradient(0, centerY - radius, 0, centerY + radius);
    gradient.addColorStop(0, "#ebebfd");
    gradient.addColorStop(0.88233, "#f7f7ff");
    gradient.addColorStop(1, "#f7f7ff");
    context.fillStyle = gradient;
  }
  context.fill();
  context.shadowColor = "transparent";
  context.lineWidth = 8;
  context.strokeStyle = "#ffffff";
  context.stroke();
  context.fillStyle = style.text;
  context.font = "700 64px 'DIN Alternate', Arial, sans-serif";
  context.textAlign = "center";
  context.textBaseline = "middle";
  const showPolarity = state !== "default" && role === "stimulation";
  if (!showPolarity) {
    context.fillText(label, centerX, centerY + 2);
  } else {
    context.font = "700 60px 'DIN Alternate', Arial, sans-serif";
    const labelWidth = context.measureText(label).width;
    context.font = "700 46px 'DIN Alternate', Arial, sans-serif";
    const polarityWidth = context.measureText(polarity).width;
    const totalWidth = labelWidth + 8 + 8 + 4 + polarityWidth;
    let cursor = centerX - totalWidth / 2;
    context.font = "700 60px 'DIN Alternate', Arial, sans-serif";
    context.textAlign = "left";
    context.fillText(label, cursor, centerY + 2);
    cursor += labelWidth + 8;
    context.beginPath();
    context.arc(cursor + 4, centerY + 2, 4, 0, Math.PI * 2);
    context.fill();
    cursor += 12;
    context.font = "700 46px 'DIN Alternate', Arial, sans-serif";
    context.fillText(polarity, cursor, centerY + 2);
  }
  const texture = new THREE.CanvasTexture(canvas);
  texture.colorSpace = THREE.SRGBColorSpace;
  texture.needsUpdate = true;
  return texture;
}

export function HeadModel({ activeRole }) {
  const mountRef = useRef(null);
  const activeRoleRef = useRef(activeRole);
  const [status, setStatus] = useState("loading");

  useEffect(() => { activeRoleRef.current = activeRole; }, [activeRole]);

  useEffect(() => {
    const mount = mountRef.current;
    if (!mount) return undefined;

    const scene = new THREE.Scene();
    const camera = new THREE.PerspectiveCamera(28, 1, 0.01, 100);
    camera.up.set(0, 0, -1);

    const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true, powerPreference: "high-performance" });
    renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    renderer.setClearColor(0x000000, 0);
    renderer.outputColorSpace = THREE.SRGBColorSpace;
    renderer.toneMapping = THREE.ACESFilmicToneMapping;
    renderer.toneMappingExposure = 1.16;
    renderer.localClippingEnabled = true;
    mount.appendChild(renderer.domElement);

    const controls = new OrbitControls(camera, renderer.domElement);
    controls.enableDamping = true;
    controls.dampingFactor = 0.07;
    controls.enablePan = false;
    controls.enableRotate = false;
    controls.minDistance = 2.8;
    controls.maxDistance = 7.5;

    scene.add(new THREE.HemisphereLight(0xffffff, 0x7688d9, 2.8));
    const keyLight = new THREE.DirectionalLight(0xffffff, 3.4);
    keyLight.position.set(-3, 5, -2);
    scene.add(keyLight);
    const rimLight = new THREE.DirectionalLight(0x8ba8ff, 5.2);
    rimLight.position.set(3, 2, 4);
    scene.add(rimLight);
    const innerGlow = new THREE.PointLight(0xb8ccff, 8, 8, 2);
    innerGlow.position.set(0, 2.4, 0.8);
    scene.add(innerGlow);

    // Keep layout placement separate from the interactive rotation pivot.
    // With camera.up pointing toward -Z, negative Z moves the whole stage up.
    const stage = new THREE.Group();
    stage.position.z = -0.19;
    scene.add(stage);
    const root = new THREE.Group();
    stage.add(root);
    const markerGroup = new THREE.Group();
    root.add(markerGroup);
    const raycaster = new THREE.Raycaster();
    const pointer = new THREE.Vector2();
    const localClipPlane = new THREE.Plane(new THREE.Vector3(0, 1, 0), 0);
    const worldClipPlane = new THREE.Plane(new THREE.Vector3(0, 1, 0), 0);
    let clippingReady = false;
    let dragState = null;
    let frameId = 0;
    let disposed = false;

    const loader = new GLTFLoader();
    loader.load(
      "/assets/head.glb",
      (gltf) => {
        if (disposed) return;
        const model = gltf.scene;
        const sourceMeshes = [];
        model.traverse((child) => {
          if (child.isMesh) sourceMeshes.push(child);
        });
        const measuredMeshes = sourceMeshes.map((mesh) => {
          const meshSize = new THREE.Box3().setFromObject(mesh).getSize(new THREE.Vector3());
          return { mesh, diagonal: meshSize.length() };
        });
        const largestDiagonal = Math.max(...measuredMeshes.map(({ diagonal }) => diagonal));
        measuredMeshes.forEach(({ mesh, diagonal }) => {
          if (diagonal < largestDiagonal * 0.35) {
            mesh.parent?.remove(mesh);
            mesh.geometry?.dispose();
            mesh.material?.dispose();
          }
        });
        model.traverse((child) => {
          if (!child.isMesh) return;
          child.material = new THREE.MeshPhysicalMaterial({
            color: 0xeaf2ff,
            roughness: 0.34,
            metalness: 0,
            transparent: true,
            opacity: 0.72,
            transmission: 0.1,
            thickness: 0.8,
            ior: 1.28,
            clearcoat: 0.58,
            clearcoatRoughness: 0.28,
            sheen: 0.72,
            sheenColor: new THREE.Color(0xb8c8ff),
            sheenRoughness: 0.48,
            specularIntensity: 0.86,
            specularColor: new THREE.Color(0xffffff),
            emissive: new THREE.Color(0x7898e8),
            emissiveIntensity: 0.045,
            clippingPlanes: [worldClipPlane],
            side: THREE.DoubleSide,
            depthWrite: true,
          });
        });

        const box = new THREE.Box3().setFromObject(model);
        const size = box.getSize(new THREE.Vector3());
        const largest = Math.max(size.x, size.y, size.z);
        // Leave a reliable safe area around every electrode at taller and
        // narrower viewport ratios, especially FP1/FP2 and O1/Oz/O2.
        const scale = 2.18 / largest;
        model.scale.setScalar(scale);
        const scaledBox = new THREE.Box3().setFromObject(model);
        const scaledCenter = scaledBox.getCenter(new THREE.Vector3());
        model.position.sub(scaledCenter);
        root.add(model);

        const normalizedBox = new THREE.Box3().setFromObject(model);
        const normalizedSize = normalizedBox.getSize(new THREE.Vector3());
        const normalizedCenter = normalizedBox.getCenter(new THREE.Vector3());
        // The GLB bounds include the lower face/neck, which pulls the visual
        // rotation centre downward. Rotate around the cranial centre instead.
        const rotationPivot = new THREE.Vector3(
          normalizedCenter.x,
          normalizedCenter.y + normalizedSize.y * 0.38,
          normalizedCenter.z - normalizedSize.z * 0.1,
        );
        model.position.sub(rotationPivot);
        const neckCutoff = normalizedBox.min.y + normalizedSize.y * 0.24;
        localClipPlane.set(new THREE.Vector3(0, 1, 0), -(neckCutoff - rotationPivot.y));
        clippingReady = true;
        const rx = normalizedSize.x * 0.4;
        const rz = normalizedSize.z * 0.38;
        const top = normalizedBox.max.y;

        ELECTRODES.forEach(([label, nx, nz]) => {
          const pointState = INITIAL_STATES[label] || "default";
          const texture = makeElectrodeTexture(label, pointState, "acquisition");
          const material = new THREE.SpriteMaterial({ map: texture, transparent: true, depthTest: false });
          const sprite = new THREE.Sprite(material);
          const x = normalizedCenter.x + nx * rx;
          const z = normalizedCenter.z + nz * rz;
          const radial = Math.min(0.96, nx * nx * 0.48 + nz * nz * 0.42);
          const y = top + 0.08 - radial * normalizedSize.y * 0.12;
          sprite.position.set(x - rotationPivot.x, y - rotationPivot.y, z - rotationPivot.z);
          sprite.scale.setScalar(0.11);
          sprite.userData = { electrode: label, state: pointState, role: "acquisition", polarity: "A" };
          markerGroup.add(sprite);
        });

        const focus = new THREE.Vector3(0, 0, 0);
        controls.target.copy(focus);
        camera.position.set(0, 2.95, 0.03);
        camera.lookAt(focus);
        controls.update();
        setStatus("ready");
      },
      undefined,
      () => setStatus("error"),
    );

    function resize() {
      const width = mount.clientWidth;
      const height = mount.clientHeight;
      if (!width || !height) return;
      renderer.setSize(width, height, false);
      camera.aspect = width / height;
      camera.updateProjectionMatrix();
    }

    function pickElectrode(event) {
      const rect = renderer.domElement.getBoundingClientRect();
      pointer.x = ((event.clientX - rect.left) / rect.width) * 2 - 1;
      pointer.y = -((event.clientY - rect.top) / rect.height) * 2 + 1;
      raycaster.setFromCamera(pointer, camera);
      const hit = raycaster.intersectObjects(markerGroup.children, false)[0];
      if (!hit) return;
      const sprite = hit.object;
      const role = activeRoleRef.current;
      sprite.material.map.dispose();
      sprite.material.map = makeElectrodeTexture(
        sprite.userData.electrode,
        sprite.userData.state,
        role,
        sprite.userData.polarity,
      );
      sprite.material.needsUpdate = true;
      sprite.userData.role = role;
    }

    function handlePointerDown(event) {
      dragState = {
        pointerId: event.pointerId,
        startX: event.clientX,
        startY: event.clientY,
        rotationX: root.rotation.x,
        rotationZ: root.rotation.z,
        moved: false,
      };
      renderer.domElement.setPointerCapture(event.pointerId);
    }

    function handlePointerMove(event) {
      if (!dragState || dragState.pointerId !== event.pointerId) return;
      const dx = event.clientX - dragState.startX;
      const dy = event.clientY - dragState.startY;
      if (Math.hypot(dx, dy) > 4) dragState.moved = true;
      const maxTilt = THREE.MathUtils.degToRad(35);
      root.rotation.x = THREE.MathUtils.clamp(dragState.rotationX + dy * 0.003, -maxTilt, maxTilt);
      root.rotation.z = THREE.MathUtils.clamp(dragState.rotationZ + dx * 0.003, -maxTilt, maxTilt);
    }

    function handlePointerUp(event) {
      if (!dragState || dragState.pointerId !== event.pointerId) return;
      if (!dragState.moved) pickElectrode(event);
      renderer.domElement.releasePointerCapture(event.pointerId);
      dragState = null;
    }

    function resetRotation() {
      root.rotation.set(0, 0, 0);
      root.position.set(0, 0, 0);
      root.scale.setScalar(1);
    }

    renderer.domElement.addEventListener("pointerdown", handlePointerDown);
    renderer.domElement.addEventListener("pointermove", handlePointerMove);
    renderer.domElement.addEventListener("pointerup", handlePointerUp);
    renderer.domElement.addEventListener("pointercancel", handlePointerUp);
    renderer.domElement.addEventListener("dblclick", resetRotation);
    const observer = new ResizeObserver(resize);
    observer.observe(mount);
    resize();

    function animate() {
      controls.update();
      if (clippingReady) {
        root.updateMatrixWorld(true);
        worldClipPlane.copy(localClipPlane).applyMatrix4(root.matrixWorld);
      }
      renderer.render(scene, camera);
      frameId = requestAnimationFrame(animate);
    }
    animate();

    return () => {
      disposed = true;
      cancelAnimationFrame(frameId);
      observer.disconnect();
      renderer.domElement.removeEventListener("pointerdown", handlePointerDown);
      renderer.domElement.removeEventListener("pointermove", handlePointerMove);
      renderer.domElement.removeEventListener("pointerup", handlePointerUp);
      renderer.domElement.removeEventListener("pointercancel", handlePointerUp);
      renderer.domElement.removeEventListener("dblclick", resetRotation);
      controls.dispose();
      scene.traverse((object) => {
        if (object.geometry) object.geometry.dispose();
        if (object.material?.map) object.material.map.dispose();
        if (object.material) object.material.dispose();
      });
      renderer.dispose();
      renderer.domElement.remove();
    };
  }, []);

  return (
    <div ref={mountRef} className="head-model" aria-label="可交互头模">
      {status === "loading" && <div className="model-status">正在加载头模…</div>}
      {status === "error" && <div className="model-status is-error">头模加载失败</div>}
      <div className="model-hint">拖动旋转 · 滚轮缩放 · 双击复位 · 点击电极配置</div>
    </div>
  );
}
