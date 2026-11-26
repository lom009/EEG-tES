import * as THREE from 'three';

// Fixed model origin in metres. Configuration uses anatomical millimetres:
// right / anterior / superior. The head faces model +Z, so wearer-right is -X.
export const MODEL_CENTER = new THREE.Vector3(0, 0.25594061613082886, 0);
export const keyOf = id => id.toUpperCase();
export function modelPosition([x, y, z]) {
  return new THREE.Vector3(-x, z, y).multiplyScalar(0.001).add(MODEL_CENTER);
}
export function modelNormal(normal, position) {
  const [x, y, z] = normal ?? position;
  const result = new THREE.Vector3(-x, z, y);
  if (![x, y, z].every(Number.isFinite) || result.lengthSq() < 1e-18)
    throw Error('无效的电极朝向');
  return result.normalize();
}

// Normalize the original electrode into a reusable local template whose face is
// at the origin and whose outward axis is +Y. Geometry is retained, not redrawn.
export function extractElectrodeTemplate(root) {
  root.updateMatrixWorld(true);
  const original = root.getObjectByName('E_Cz');
  const face = original?.getObjectByName('E_Cz_Electrode_Face');
  if (!face) throw Error('模型缺少电极造型模板');
  const anchor = face.getWorldPosition(new THREE.Vector3());
  const rotation = face.getWorldQuaternion(new THREE.Quaternion());
  const inverse = new THREE.Matrix4().compose(anchor, rotation, new THREE.Vector3(1, 1, 1)).invert();
  const template = new THREE.Group();
  original.traverse(mesh => {
    if (!mesh.isMesh) return;
    const geometry = mesh.geometry.clone().applyMatrix4(new THREE.Matrix4().multiplyMatrices(inverse, mesh.matrixWorld));
    const child = new THREE.Mesh(geometry, mesh.material);
    child.name = mesh.name.replace('E_Cz_', '');
    template.add(child);
  });
  const remove = [];
  root.traverse(object => {
    if (/^E_[^_]+$/.test(object.name) || /CAP_Cables$|CAP_Cable_Anchor|CAP_Seam_Cable_Clip/.test(object.name))
      remove.push(object);
  });
  for (const object of remove) object.removeFromParent();
  return template;
}

export function placeElectrode(template, point) {
  if (!point.position?.every(Number.isFinite) || point.position.length !== 3)
    throw Error(`${point.id}：缺少或无效的三维位置`);
  const group = template.clone(true);
  group.name = `Electrode:${point.id}`;
  group.userData.electrodeId = point.id;
  group.position.copy(modelPosition(point.position));
  group.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), modelNormal(point.normal, point.position));
  group.traverse(mesh => { if (mesh.isMesh) mesh.material = mesh.material.clone(); });
  return group;
}
