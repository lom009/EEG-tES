export const DEMO_SENTENCES = [
  '今天的阳光真好', '窗外的小鸟在唱歌', '我们一起慢慢练习', '公园里面开满鲜花',
  '妈妈正在准备晚饭', '小朋友们喜欢画画', '请把桌上的书给我', '明天我们去看电影',
  '早晨的空气很清新', '这杯温水刚刚合适', '爸爸每天坚持散步', '小猫安静地睡着了',
  '花园里的树长高了', '请你慢慢说一遍', '今天我们学得很好', '窗外下起了小雨',
  '朋友送来一本新书', '大家一起整理房间', '这条小路通向学校', '我们完成今天练习',
];

// One source coordinate map for every state of this Figma demonstration.
// Coordinates are read from 549:6287 relative to the 1440 × 900 frame.
export const DEMO_POINTS = [
  ['Cz',501,528],['C3',399,528],['C5',337,528],['T7',276,528],
  ['CP4',580,528],['CP6',651,529],['T8',738,528],['TP7',295,622],
  ['CP5',352,609],['CP3',410,603],['CP4',574,603],['CP6',631,609],['TP8',689,622],
  ['FT7',295,434],['FC5',352,444],['FC3',410,453],['FCz\nREF',501,453],
  ['AFz\nGND',501,315],['FC6',641,444],['FC4',580,455],['FT8',699,434],
  ['P7',329,716],['F7',329,347],['P8',664,716],['F8',664,347],['O1',429,780],
  ['Pz',501,679],['Fz',501,384],['P3',415,690],['F3',415,373],['P4',583,690],
  ['F4',578,374],['O2',574,781],['FP1',429,264],['FP2',574,265],
];
export const SUPPORTED_POINTS = ['FP2','F3','FC5','T7','Cz','T8','CP5','P3'];
export const asset = (name) => window.__SINGLE_STIM_ASSETS__?.[name] || `/assets/single-stimulation/${name}`;
export const timeLabel = (seconds) => `${String(Math.floor(seconds / 60)).padStart(2,'0')}:${String(Math.floor(seconds % 60)).padStart(2,'0')}`;
export const allScored = (scores) => scores.length > 0 && scores.every(value => value === true || value === false);
export const canConfirmElectrodes = (points, channels, impedance) => Boolean(points.A && points.C && points.A !== points.C && channels.A !== channels.C && impedance === 'passed');
