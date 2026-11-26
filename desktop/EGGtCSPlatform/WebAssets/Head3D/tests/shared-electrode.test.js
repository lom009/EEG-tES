import test from 'node:test';
import assert from 'node:assert/strict';
import { renderElectrodeLabel, placeElectrodeLabel } from '../electrode-visuals.js';

function button() {
  return {
    children: [], classes: new Set(),
    ownerDocument: { createElement: () => ({ style: {} }) },
    classList: { toggle(name, enabled) { this.owner.classes[enabled ? 'add' : 'delete'](name); } },
    replaceChildren() { this.children = []; this.textContent = ''; },
    append(child) { this.children.push(child); },
  };
}

test('shared labels preserve both roles and independent colors across updates', () => {
  const element = button();
  element.classList.owner = element;
  const point = { shared: true, label: 'CP10·C', acquisitionLabel: '采',
    stimulationColor: '#E91919', acquisitionColor: '#38A169',
    stimulationForeground: '#FFFFFF', acquisitionForeground: '#FFFFFF', tooltip: '刺激\n采集' };
  renderElectrodeLabel(element, point, 'CP10');
  assert.deepEqual(element.children.map(x => x.textContent), ['CP10·C', '采']);
  assert.deepEqual(element.children.map(x => x.style.background), ['#E91919', '#38A169']);
  assert.equal(element.title, '刺激\n采集');
  assert.equal(element.classes.has('shared-divider'), false);
  assert.deepEqual(element.children.map(x => x.style.color), ['#FFFFFF', '#FFFFFF']);
  renderElectrodeLabel(element, { ...point, acquisitionLabel: 'REF', acquisitionColor: '#E9EBFF' }, 'CP10');
  assert.equal(element.children.length, 2);
  assert.equal(element.children[0].style.background, '#E91919');
  assert.equal(element.children[1].textContent, 'REF');
  renderElectrodeLabel(element, { ...point, showSharedDivider: true }, 'CP10');
  assert.equal(element.classes.has('shared-divider'), true);
  renderElectrodeLabel(element, { label: 'CP10·C', shared: false }, 'CP10');
  assert.equal(element.children.length, 0);
  assert.equal(element.classes.has('shared'), false);
  assert.equal(element.textContent, 'CP10·C');
});

test('shared circular labels reserve their full size in collision layout', () => {
  const args = { anchor: { x: 120, y: 120 }, stageCenter: { x: 200, y: 120 },
    viewport: { width: 400, height: 400 }, labelWidth: 54, labelHeight: 54 };
  const first = placeElectrodeLabel({ ...args, occupied: [] });
  const second = placeElectrodeLabel({ ...args, occupied: [first] });
  assert.ok(first && second);
  assert.equal(first.width, 54);
  assert.ok(Math.abs(first.x - second.x) >= 61 || Math.abs(first.y - second.y) >= 61);
});
