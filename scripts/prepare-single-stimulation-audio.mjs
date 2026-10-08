import { execFileSync } from 'node:child_process';
import { mkdir, unlink } from 'node:fs/promises';
import { DEMO_SENTENCES } from '../src/components/singleStimulationDemoData.js';
await mkdir('public/assets/single-stimulation', {recursive:true});
for (let i=0;i<DEMO_SENTENCES.length;i++) {
  const temporary = `/tmp/eeg-tes-single-stimulation-${i+1}.aiff`;
  execFileSync('/usr/bin/say',['-v','Tingting','-r','140','-o',temporary,DEMO_SENTENCES[i]]);
  execFileSync('/usr/bin/afconvert',['-f','WAVE','-d','LEI16',temporary,`public/assets/single-stimulation/sentence-${i+1}.wav`]);
  await unlink(temporary);
  console.log(`Prepared sentence ${i+1}`);
}
