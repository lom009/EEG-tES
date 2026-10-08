import * as THREE from 'three';

// The supplied head mesh spans world Y=0..0.32 m. Keep the face opaque and
// fade only the neck below the jaw; both passes share the same 3D geometry.
export const NECK_FADE_BOTTOM_Y = 0.035;
export const NECK_FADE_UPPER_Y = 0.145;

export function applyNeckFade(headMesh) {
  const colorMaterial = headMesh.material;
  colorMaterial.transparent = true;
  colorMaterial.depthWrite = false;
  colorMaterial.depthFunc = THREE.EqualDepth;
  colorMaterial.onBeforeCompile = (shader) => {
    shader.vertexShader = shader.vertexShader
      .replace('#include <common>', '#include <common>\nvarying float vNeckWorldY;')
      .replace(
        '#include <worldpos_vertex>',
        '#include <worldpos_vertex>\nvNeckWorldY = (modelMatrix * vec4(transformed, 1.0)).y;',
      );
    shader.fragmentShader = shader.fragmentShader
      .replace('#include <common>', '#include <common>\nvarying float vNeckWorldY;')
      .replace(
        '#include <opaque_fragment>',
        `diffuseColor.a *= smoothstep(${NECK_FADE_BOTTOM_Y.toFixed(3)}, ${NECK_FADE_UPPER_Y.toFixed(3)}, vNeckWorldY);\n#include <opaque_fragment>`,
      );
  };
  colorMaterial.customProgramCacheKey = () =>
    `neck-fade-${NECK_FADE_BOTTOM_Y}-${NECK_FADE_UPPER_Y}`;
  colorMaterial.needsUpdate = true;

  // Depth-only pass hides interior triangles without painting the page background.
  const colorMesh = headMesh.clone(false);
  colorMesh.name = 'HEAD_FADE_COLOR';
  colorMesh.castShadow = false;
  headMesh.material = new THREE.MeshBasicMaterial({
    colorWrite: false,
    depthWrite: true,
    side: THREE.FrontSide,
  });
  return colorMesh;
}
