import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import * as THREE from 'three';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { MODEL_CENTER, modelPosition, modelNormal, extractElectrodeTemplate, placeElectrode } from '../electrode-layout.js';

const config = JSON.parse(fs.readFileSync(new URL('../../../appsettings.default.json', import.meta.url)));
const points = config.ElectrodePositions.Positions;
const positionOf = id => { const p = points.find(p=>p.Id===id).Position3D; return modelPosition([p.X,p.Y,p.Z]); };

test('wearer left/right and anterior/posterior agree in all camera orientations', () => {
  for (const [left,right] of [['F3','F4'],['FP1','FP2'],['C3','C4'],['O1','O2']]) {
    const l=positionOf(left), r=positionOf(right);
    assert.ok(l.x > 0 && r.x < 0, `${left}/${right}: anatomical hemispheres`);
    for (const [eye,up,leftOnScreen] of [
      [[0,0,1],[0,1,0],false], [[0,0,-1],[0,1,0],true], [[0,1,0],[0,0,1],true],
    ]) {
      const camera=new THREE.PerspectiveCamera(45,1,0.001,10);
      camera.position.copy(MODEL_CENTER).add(new THREE.Vector3(...eye));
      camera.up.set(...up); camera.lookAt(MODEL_CENTER); camera.updateMatrixWorld();
      const lx=l.clone().project(camera).x, rx=r.clone().project(camera).x;
      assert.equal(lx < rx,leftOnScreen,`${left}/${right}: ${eye}`);
    }
    for (const side of [-1,1]) {
      const camera=new THREE.PerspectiveCamera(45,1,0.001,10);
      camera.position.copy(MODEL_CENTER).add(new THREE.Vector3(side,0,0));
      camera.lookAt(MODEL_CENTER); camera.updateMatrixWorld();
      assert.equal(l.clone().project(camera).z < r.clone().project(camera).z,side===1,
        `${left}/${right}: closest hemisphere from side ${side}`);
    }
  }
  assert.ok(positionOf('FP1').z > 0 && positionOf('O1').z < 0);
  assert.ok(positionOf('Cz').y > MODEL_CENTER.y);
});

test('normal fallback follows anatomical coordinates and rejects invalid direction', () => {
  assert.deepEqual(modelNormal(null,[-10,0,0]).toArray(),[1,0,0]);
  assert.throws(()=>modelNormal([0,0,0],[1,1,1]));
  assert.throws(()=>modelNormal([NaN,1,1],[1,1,1]));
});

test('real GLB template supports arbitrary counts and positions without baked electrodes', async () => {
  const b=fs.readFileSync(new URL('../eeg-head.glb',import.meta.url));
  // Node verifies geometry; browser visual tests cover the embedded image textures.
  const loader=new GLTFLoader().register(()=>({name:'geometry-test',loadTexture:()=>Promise.resolve(null)}));
  const gltf=await loader.parseAsync(b.buffer.slice(b.byteOffset,b.byteOffset+b.byteLength),'');
  const template=extractElectrodeTemplate(gltf.scene);
  assert.equal(template.children.length,3);
  assert.ok(gltf.scene.getObjectByName('HEAD'));
  assert.ok(gltf.scene.getObjectByName('CAP'));
  gltf.scene.traverse(o=>assert.ok(!/^E_|CAP_Cables$|CAP_Cable_Anchor|CAP_Seam_Cable_Clip/.test(o.name),o.name));
  for (const count of [2,34,40]) {
    const group=new THREE.Group();
    for(let i=0;i<count;i++) group.add(placeElectrode(template,{id:`custom-${i}`,position:[i+1,20,50]}));
    assert.equal(group.children.length,count);
    const first=group.children[0];
    assert.deepEqual(first.position.toArray(),modelPosition([1,20,50]).toArray());
    const face=first.getObjectByName('Electrode_Face');
    group.updateMatrixWorld(true);
    assert.ok(face.getWorldPosition(new THREE.Vector3()).distanceTo(first.position)<1e-7);
    const actualUp=new THREE.Vector3(0,1,0).applyQuaternion(first.quaternion);
    assert.ok(actualUp.distanceTo(modelNormal(null,[1,20,50]))<1e-7);
    assert.notEqual(first.children[0].material,group.children[1].children[0].material);
  }
});
