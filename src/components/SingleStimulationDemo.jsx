import { useEffect, useRef, useState } from 'react';
import { DEMO_SENTENCES, DEMO_POINTS, SUPPORTED_POINTS, asset, timeLabel, allScored, canConfirmElectrodes } from './singleStimulationDemoData.js';
import './SingleStimulationDemo.css';

const STORAGE_KEY = 'eeg-tes-single-stimulation-demo-results-v1';
const ICONS = { back:'546a4.svg', check:'9ed52.svg', close:'85551.svg', refresh:'8d643.svg', info:'eb10d.svg', warn:'8bb34.svg', confirm:'26439.svg', detect:'15bd5.svg', plus:'3b1c7.svg', minus:'0b823.svg', correct:'ad98f.svg', wrong:'5c4d6.svg', circle:'ad98f.svg', play:'b18b3.svg', flag:'e4a30.svg', pie:'10430.svg', reset:'3d20c.svg', action:'f8d18.svg' };
function ScoreIcon({ correct, selected, enabled }) {
  const icon = !enabled ? `score-${correct ? 'correct' : 'wrong'}-disabled.svg`
    : selected ? (correct ? 'ad98f.svg' : '5c4d6.svg')
    : `score-${correct ? 'correct' : 'wrong'}-enabled.svg`;
  return <img className="ssd-icon" src={asset(icon)} alt="" />;
}
function Icon({ name, className = '' }) { return <img className={`ssd-icon ${className}`} src={asset(ICONS[name])} alt="" />; }
function Button({ children, tone = '', icon, className = '', ...props }) { return <button type="button" className={`ssd-button ${tone} ${className}`} {...props}>{icon && <Icon name={icon} />}{children}</button>; }
function Dialog({ title, children, onClose, footer }) { return <div className="ssd-scrim" role="presentation"><section className="ssd-dialog" role="dialog" aria-modal="true" aria-label={title}><header><h2>{title}</h2><Button aria-label="关闭弹窗" icon="close" onClick={onClose} /></header><div className="ssd-dialog-body">{children}</div><footer>{footer}</footer></section></div>; }
function Choice({ label, value, options, onChange, disabled }) {
  const [open,setOpen] = useState(false);
  const box = useRef(null);
  useEffect(() => { const close = e => { if (!box.current?.contains(e.target)) setOpen(false); }; document.addEventListener('pointerdown',close); return () => document.removeEventListener('pointerdown',close); },[]);
  return <div ref={box} className="ssd-choice"><button type="button" aria-label={label} aria-haspopup="listbox" aria-expanded={open && !disabled} disabled={disabled} onClick={()=>setOpen(!open)}>{value}<img src={asset('3ce42.svg')} alt="" /></button>{open && !disabled && <div className="ssd-options" role="listbox" aria-label={label}>{options.map(option=><button type="button" role="option" aria-selected={option === value} key={option} onClick={()=>{onChange(option);setOpen(false);}}>{option}{option===value && <Icon name="check" />}</button>)}</div>}</div>;
}
function Header({onHelp}) { return <header className="ssd-header"><div className="ssd-brand"><img src={asset('4aed1.svg')} alt=""/><img src={asset('daff8.svg')} alt="EEG-tES"/></div><div className="ssd-device"><img src={asset('075ef.svg')} alt="刺激设备电源"/><img src={asset('a0cfe.svg')} alt="设备电量"/><span><img src={asset('fa628.svg')} alt=""/>未连接</span><span className="connected"><img src={asset('256ff.svg')} alt=""/>已连接</span><button className="ssd-avatar" onClick={onHelp} aria-label="演示说明">TA<i/></button></div></header>; }
function Slider({label,value,max,step,unit,onChange}) { return <div className="ssd-slider"><label>{label}</label><div><small>0</small><input aria-label={`${label}滑块`} type="range" min="0" max={max} step={step} value={value} onChange={e=>onChange(Number(e.target.value))} style={{'--fill':`${value/max*100}%`}}/><small>{max}</small><label className="ssd-number"><input aria-label={label} type="number" min="0" max={max} step={step} value={value} onChange={e=>onChange(e.target.value === '' ? '' : Number(e.target.value))}/><small>{unit}</small></label></div></div>; }
function PlanWave({ current }) {
  const amplitude = Math.max(0,Math.min(2,Number(current)||0));
  const samples = Array.from({length:241},(_,index)=>{
    const time=index/24;
    const irregular=
      Math.sin(2*Math.PI*(.61*time+.018*time*time))*.58+
      Math.sin(2*Math.PI*1.37*time+.72)*.23+
      Math.sin(2*Math.PI*.19*time-1.1)*.14;
    const value=amplitude*Math.max(-1,Math.min(1,irregular));
    return [time*100,110-value/2*100];
  });
  const line=samples.map(([x,y],index)=>`${index?'L':'M'}${x.toFixed(2)} ${y.toFixed(2)}`).join(' ');
  const area=`${line} L1000 110 L0 110 Z`;
  return <svg className="ssd-plan-wave" viewBox="0 0 1000 220" preserveAspectRatio="none" role="img" aria-label={`0 到 10 秒、正负 ${amplitude} mA 的不规则双向刺激波形示意`}>
    <defs><linearGradient id="ssd-plan-wave-fill" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stopColor="#8f3dff" stopOpacity=".22"/><stop offset="1" stopColor="#8f3dff" stopOpacity=".03"/></linearGradient></defs>
    <line x1="0" y1="110" x2="1000" y2="110" className="ssd-plan-zero"/>
    <path d={area} fill="url(#ssd-plan-wave-fill)"/>
    <path d={line} className="ssd-plan-wave-line"/>
  </svg>;
}
function WaveChart({shown,progress,delay,duration,current,replay}) {
  const total = duration + delay / 1000;
  const audioProgress = Math.min(1,progress / duration);
  const stimProgress = Math.max(0,Math.min(1,(progress-delay/1000)/duration));
  return <div className="ssd-chart-box">
    {[false,true].map(stim=>{const reveal=stim?(replay?1:stimProgress):audioProgress;const revealStyle={clipPath:`inset(0 ${(1-reveal)*100}% 0 0)`};return <div className={`ssd-chart-row ${stim?'stimulation':''}`} key={String(stim)}>
      <span className="ssd-axis-unit">{stim?'mA':'Amplitude'}</span><div className="ssd-chart-name">{stim?'刺激波形':'语音包络'}</div>
      <div className="ssd-y-labels">{(stim?[current,current/2,0,-current/2,-current]:[0.4,0.2,0,-0.2,-0.4]).map((n,i)=><span key={i}>{Number(Number(n).toFixed(2))}</span>)}</div>
      <div className="ssd-grid">
        {shown && (stim
          ? <img className="ssd-complete-stim-wave" src={asset('e6471.svg')} alt="完整刺激波形" style={{...revealStyle,opacity:replay?.32:1}}/>
          : <><img className="ssd-wave-raw" src={asset('03a27.svg')} alt="完整语音波形" style={revealStyle}/><img className="ssd-complete-audio-envelope" src={asset('29241.svg')} alt="完整语音包络" style={revealStyle}/></>)}
        {shown && (!stim || !replay) && <i className={`ssd-cursor ${stim?'purple':''}`} style={{left:`${(stim?stimProgress:audioProgress)*100}%`}}/>}
      </div>
    </div>})}
    <div className="ssd-x-labels"><small>时间(s)</small>{Array.from({length:9},(_,i)=><span key={i}>{Number((total*i/8).toFixed(1))}</span>)}</div>
  </div>;
}
export function SingleStimulationDemo({onHome}) {
  const [scale,setScale]=useState(()=>Math.min(window.innerWidth/1440,window.innerHeight/900));
  const [page,setPage]=useState('plan');
  const [current,setCurrent]=useState(2), [delay,setDelay]=useState(40);
  const [role,setRole]=useState('A'), [points,setPoints]=useState({A:null,C:null}), [channels,setChannels]=useState({A:'CH2',C:'CH4'});
  const [impedance,setImpedance]=useState('idle'), [failNext,setFailNext]=useState(false), [showStim,setShowStim]=useState(true), [showAcq,setShowAcq]=useState(true);
  const [trial,setTrial]=useState(0), [run,setRun]=useState('ready'), [scores,setScores]=useState(Array(DEMO_SENTENCES[0].length).fill(null));
  const [corpus,setCorpus]=useState('MSP'), [sentenceList,setSentenceList]=useState('1'), [testMode,setTestMode]=useState('固定语速测试（安静）'), [speechType,setSpeechType]=useState('原始语音'), [rate,setRate]=useState(0);
  const [progress,setProgress]=useState(0), [duration,setDuration]=useState(8), [replaying,setReplaying]=useState(false), [audioReady,setAudioReady]=useState(false);
  const [records,setRecords]=useState([]), [dialog,setDialog]=useState(null), [notice,setNotice]=useState('');
  const [helpVisible,setHelpVisible]=useState(false), [inspectRecord,setInspectRecord]=useState(null);
  const audioRef=useRef(null), raf=useRef(0), tailTimer=useRef(null), detectionTimer=useRef(null), replayReturn=useRef('scoring');
  const sentence=DEMO_SENTENCES[trial];
  const graded=scores.filter(v=>v!==null).length, correct=scores.filter(v=>v===true).length;
  const scoring=run==='scoring', active=run==='running'||replaying;
  const planValid=Number(current)>0 && Number(current)<=2 && delay!=='' && Number(delay)>=0 && Number(delay)<=300;
  const electrodesValid=canConfirmElectrodes(points,channels,impedance);
  const flash=(message)=>setNotice(message);
  useEffect(()=>{const resize=()=>setScale(Math.min(window.innerWidth/1440,window.innerHeight/900));window.addEventListener('resize',resize);return()=>window.removeEventListener('resize',resize);},[]);
  useEffect(()=>{if(!notice)return;const t=setTimeout(()=>setNotice(''),4000);return()=>clearTimeout(t);},[notice]);
  useEffect(()=>()=>{cancelAnimationFrame(raf.current);clearTimeout(tailTimer.current);clearTimeout(detectionTimer.current);audioRef.current?.pause();},[]);
  useEffect(()=>{setAudioReady(false);},[trial]);
  const invalidate=()=>{clearTimeout(detectionTimer.current);setImpedance('idle');};
  const assign=(point)=>{if(impedance==='checking')return;if(points[role==='A'?'C':'A']===point){flash('同一个点位不能同时作为阳极和阴极');return;}setPoints(p=>({...p,[role]:p[role]===point?null:point}));invalidate();};
  const detect=()=>{if(impedance==='checking'){clearTimeout(detectionTimer.current);setImpedance('idle');return;}if(!points.A||!points.C){flash('请先分别选择阳极与阴极');return;}if(channels.A===channels.C){flash('阳极和阴极需要使用不同物理通道');return;}setImpedance('checking');detectionTimer.current=setTimeout(()=>{setImpedance(failNext?'failed':'passed');setFailNext(false);},1800);};
  const resetTrial=(index)=>{setTrial(index);setRun('ready');setScores(Array(DEMO_SENTENCES[index].length).fill(null));setProgress(0);setReplaying(false);};
  const stopPlayback=()=>{cancelAnimationFrame(raf.current);clearTimeout(tailTimer.current);audioRef.current?.pause();setReplaying(false);};
  const start=async(preview=false)=>{
    if(active||!audioReady)return;
    if(!preview&&!electrodesValid){flash('请先完成有效的刺激阻抗检测');return;}
    const a=audioRef.current; a.currentTime=0;a.playbackRate=rate>0?Math.min(2,Math.max(.5,Number(rate)/3)):1;
    setProgress(0);replayReturn.current=run;
    if(preview)setReplaying(true);else {setRun('running');setScores(Array(sentence.length).fill(null));}
    try{await a.play();const tick=()=>{setProgress(a.currentTime);if(!a.paused)raf.current=requestAnimationFrame(tick);};raf.current=requestAnimationFrame(tick);}
    catch{setReplaying(false);setRun(preview?replayReturn.current:'ready');flash('音频未能播放，请重新点击播放');}
  };
  const audioEnded=()=>{cancelAnimationFrame(raf.current);if(replaying){setReplaying(false);setProgress(duration);return;}if(run!=='running')return;const started=performance.now();const tick=()=>{const elapsed=(performance.now()-started)/1000;setProgress(duration+Math.min(delay/1000,elapsed));if(elapsed<delay/1000)raf.current=requestAnimationFrame(tick);else setRun('scoring');};raf.current=requestAnimationFrame(tick);};
  const stop=()=>{stopPlayback();setRun('aborted');flash('本句已中止，音频与刺激演示均已停止');};
  const save=()=>{if(!allScored(scores)||!scoring)return;const record={trial:trial+1,sentence,scores:[...scores],correct,total:scores.length,current,delay,points:{...points},channels:{...channels},settings:{corpus,sentenceList,testMode,speechType,rate},savedAt:new Date().toISOString()};const next=[...records.filter(r=>r.trial!==record.trial),record];setRecords(next);setRun('saved');try{localStorage.setItem(STORAGE_KEY,JSON.stringify(next));flash('本句结果已保存，可进入下一句');}catch{flash('本句结果已保留，请在结束时导出结果');}};
  const finish=()=>{stopPlayback();setDialog(null);setPage('results');setRun('finished');};
  const exportResults=()=>{const data={demo:true,experimentId:'EXP-20260920-001',paradigm:'包络-tACS',records};const url=URL.createObjectURL(new Blob([JSON.stringify(data,null,2)],{type:'application/json'}));const a=document.createElement('a');a.href=url;a.download='包络tACS-演示实验结果.json';a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);};
  const back=()=>{if(page==='plan'){setDialog('leave');return;}if(active){flash('请先停止当前播放与刺激');return;}if(page==='electrodes')setPage('plan');else if(page==='training')setDialog('back');else setPage('training');};
  const phase=active?(replaying?'仅重播音频':'播放与刺激中'):({ready:'待开始',scoring:'等待回答与评分',saved:'本句已保存',aborted:'本句已中止',finished:'实验已结束'}[run]);
  return <div className="ssd-viewport"><main className="ssd-app" style={{transform:`scale(${scale})`}}>
    <Header onHelp={()=>setHelpVisible(true)}/>
    <nav className="ssd-breadcrumb"><Button icon="back" onClick={back}>返回/{page==='plan'?'刺激参数配置':page==='electrodes'?'电极与检测':'单刺激言语训练'}</Button><span>患者ID：EXP-20260707-001</span><span>实验ID：EXP-20260920-001</span><span>单刺激</span></nav>
    {page==='plan' && <><aside className="ssd-paradigm"><b>包络-tACS</b><small>正弦交流刺激</small></aside><section className="ssd-plan ssd-panel"><div className="ssd-section-heading"><h2>刺激范式：包络-tACS</h2><Button tone="primary" icon="confirm" disabled={!planValid} onClick={()=>setPage('electrodes')}>进入电极配置与检测</Button></div><section className="ssd-bordered"><h2>参数设置</h2><div className="ssd-sliders"><Slider label="最大电流" value={current} max={2} step={.1} unit="mA" onChange={v=>{setCurrent(v);invalidate();}}/><Slider label="刺激启动延时" value={delay} max={300} step={1} unit="ms" onChange={v=>{setDelay(v);invalidate();}}/></div>{!planValid&&<p className="ssd-error">请设置大于 0 且不超过 2 mA 的最大电流，刺激启动延时范围为 0–300 ms。</p>}</section><section className="ssd-bordered"><h2>示意图</h2><div className="ssd-plan-chart"><span className="ssd-plan-unit">电流(mA)</span><div className="ssd-plan-y">{[2,1,0,-1,-2].map(v=><span key={v}>{v}</span>)}</div><div className="ssd-plan-grid"><PlanWave current={current}/></div><div className="ssd-plan-x">{Array.from({length:11},(_,i)=><span key={i}>{i}</span>)}</div><small className="ssd-time-unit">时间(s)</small></div></section></section></>}
    {page==='electrodes' && <>
      <img className="ssd-head-background" src={asset('1b19f.png')} alt=""/><div className="ssd-head-visual"><img className="ssd-head-image" src={asset('7127a.png')} alt="头部电极位置模型"/><img className="ssd-head-cross" src={asset('344db.svg')} alt=""/></div>
      <div className="ssd-view-controls"><Button icon="refresh" onClick={()=>flash('当前为 Figma 后视角点位图')}>后视角</Button><label><input type="checkbox" checked={showAcq} onChange={e=>setShowAcq(e.target.checked)}/>查看采集电极</label><label><input type="checkbox" checked={showStim} onChange={e=>setShowStim(e.target.checked)}/>查看刺激电极</label></div>
      <p className="ssd-supported"><Icon name="warn"/>目前刺激只支持 FP2、F3、FC5、T7、Cz、T8、CP5、P3 点位</p>
      <div className="ssd-electrodes">{DEMO_POINTS.map(([label,x,y],i)=>{const supported=SUPPORTED_POINTS.includes(label);const assigned=points.A===label?'A':points.C===label?'C':null;const color=assigned?(impedance==='passed'?(assigned==='A'?'#38a169':'#2f86ff'):impedance==='failed'?(assigned==='A'?'#e91919':'#2f86ff'):'#2f86ff'):null;return <button key={`${label}-${i}`} type="button" aria-label={`点位 ${label}${assigned?`·${assigned}`:''}`} title={supported?`点击分配为${role==='A'?'阳极':'阴极'}`:'该点位暂不支持刺激'} disabled={!supported||impedance==='checking'} className={`ssd-electrode ${supported?'supported':''} ${assigned?'assigned':''} ${impedance==='checking'&&assigned?'checking':''}`} style={{left:x,top:y,background:color||undefined,color:assigned?'white':undefined,opacity:assigned&&!showStim?.3:!supported?.35:1}} onClick={()=>assign(label)}>{label}{assigned&&`·${assigned}`}</button>;})}</div>
      <section className="ssd-legend"><h3>阻抗图例</h3>{[['≤10 kΩ','优','#38a169'],['10～20 kΩ','良','#2f86ff'],['20～30 kΩ','中','#f3a619'],['30～40 kΩ','差','#fd5b38'],['>40 kΩ','不良','#e91919']].map(([range,text,color])=><div key={range}><i style={{background:color}}/><span>{range}</span><span>{text}</span></div>)}</section>
      <aside className="ssd-panel ssd-electrode-panel"><div className="ssd-section-heading"><h2>单刺激模式</h2><small><Icon name="info"/>点击头模点位赋予所选角色</small></div><div className="ssd-role-switch">{[['A','阳极','plus'],['C','阴极','minus']].map(([r,label,icon])=><button key={r} className={role===r?'selected':''} onClick={()=>setRole(r)} disabled={impedance==='checking'}><Icon name={icon}/>{label}{role===r&&<span>●</span>}</button>)}</div><div className="ssd-paradigm-row"><span>刺激范式</span><span>包络-tACS</span></div><div className="ssd-section-heading"><h2>刺激阻抗状态</h2>{impedance==='passed'&&<small className="success">● 已通过</small>}{impedance==='failed'&&<small className="ssd-error">● 未通过</small>}<Button icon="detect" tone={impedance==='checking'?'danger-outline':'outline'} disabled={!points.A||!points.C} onClick={detect}>{impedance==='checking'?'停止检测':impedance==='failed'?'重新检测':'检测'}</Button></div>
        {points.A||points.C?<div className="ssd-impedance-list">{['A','C'].filter(r=>points[r]).map(r=><div className="ssd-impedance-item" key={r}><i style={{background:impedance==='failed'&&r==='A'?'#e91919':impedance==='passed'&&r==='A'?'#38a169':'#2f86ff'}}/><b>{points[r]}·{r}</b><Choice label={`${r==='A'?'阳极':'阴极'}通道`} value={channels[r]} options={Array.from({length:8},(_,i)=>`CH${i+1}`)} disabled={impedance==='checking'} onChange={v=>{setChannels(p=>({...p,[r]:v}));invalidate();}}/><span>{impedance==='checking'?'检测中':impedance==='passed'?(r==='A'?'8.1 kΩ':'13 kΩ'):impedance==='failed'?(r==='A'?'45 kΩ':'13 kΩ'):'—'}</span><span className={impedance==='failed'&&r==='A'?'ssd-error':'success'}>{impedance==='passed'?(r==='A'?'优':'良'):impedance==='failed'?(r==='A'?'不良':'良'):'待测'}</span><button className="ssd-remove" aria-label={`移除${points[r]}`} disabled={impedance==='checking'} onClick={()=>{setPoints(p=>({...p,[r]:null}));invalidate();}}>×</button></div>)}{impedance==='failed'&&<p className="ssd-error ssd-detection-note">请调整电极接触后重新检测。演示中再次检测将通过。</p>}{channels.A===channels.C&&<p className="ssd-error">阳极与阴极不能使用同一通道。</p>}</div>:<div className="ssd-empty"><img src={asset('52cf8.png')} alt=""/><p>未开始检测</p><small>请先点击头模点位，开始检测阻抗状态</small></div>}
        <Button className="ssd-bottom" tone="primary" icon="confirm" disabled={!electrodesValid} onClick={()=>setDialog('confirm')}>查看并确认实验配置</Button>
      </aside>
    </>}
    {page==='training' && <>
      <section className="ssd-panel ssd-training"><div className="ssd-section-heading"><h2>音频执行</h2><small className="ssd-pill"><Icon name="info"/>{active?(replaying?'仅重播音频，不执行刺激':'音频与刺激执行中，请等待完整播放'):run==='ready'?'请完整播放，结束后开放评分':'题目音频可重复播放'}</small><button type="button" className="ssd-audio-control" disabled={active||!audioReady} onClick={()=>start(true)} aria-label="重播题目音频"><b>题目音频</b>{timeLabel(progress)}/{timeLabel(duration)}<Icon name="play"/></button></div>
        <WaveChart shown={progress>0||run!=='ready'} progress={progress} delay={Number(delay)} duration={duration} current={Number(current)} replay={replaying}/>
        <div className="ssd-section-heading ssd-training-title"><h2>言语训练</h2><span className="ssd-pill">{corpus}· 句表 {sentenceList}</span><small className="ssd-pill instruction"><Icon name="info"/>{active?'请等待音频和刺激结束':run==='saved'?'本句结果已保存，可进入下一句':'请根据患者的发音表现，对每个字进行评分'}</small><span className="ssd-trial-number">第 <b>{String(trial+1).padStart(2,'0')}</b> / 20 句</span></div>
        <div className="ssd-stats">{[['flag','已评',`${graded}/${scores.length}`,''],['check','正确',correct,'success'],['close','错误',graded-correct,'ssd-error'],['pie','正确率',allScored(scores)?`${Math.round(correct/scores.length*100)}%`:'待判定','']].map(([icon,label,value,cls])=><div key={label}><Icon name={icon}/><span>{label}</span><b className={cls}>{value}</b></div>)}</div>
        <div className="ssd-word-cards">{Array.from(sentence).map((word,index)=><article className={`ssd-word ${scores[index]===true?'right':scores[index]===false?'wrong':''}`} key={`${trial}-${index}`}><b>{word}</b><div><button aria-label={`第${index+1}字${word}错误`} aria-pressed={scores[index]===false} disabled={!scoring||active} onClick={()=>setScores(s=>s.map((v,i)=>i===index?false:v))}><ScoreIcon correct={false} selected={scores[index]===false} enabled={scoring&&!active}/></button><button aria-label={`第${index+1}字${word}正确`} aria-pressed={scores[index]===true} disabled={!scoring||active} onClick={()=>setScores(s=>s.map((v,i)=>i===index?true:v))}><ScoreIcon correct selected={scores[index]===true} enabled={scoring&&!active}/></button></div></article>)}</div>
        <div className="ssd-grading-footer"><small className="ssd-hint"><Icon name="warn"/>{run==='saved'?'本句结果已保存':run==='aborted'?'本句已中止，请重新执行本句':active?'需要完整播放音频与刺激，结束后才可评分':'等待患者口头回答后，进行评分，完成后可保存并进入下一句'}</small><div><Button icon="check" disabled={!scoring||active} onClick={()=>setScores(Array(sentence.length).fill(true))}>全对</Button><Button icon="close" disabled={!scoring||active} onClick={()=>setScores(Array(sentence.length).fill(false))}>全错</Button><Button icon="reset" disabled={!scoring||active} onClick={()=>setScores(Array(sentence.length).fill(null))}>重置</Button></div></div>
      </section>
      <aside className="ssd-panel ssd-training-panel"><h2>包络-tACS 刺激方案</h2><div className="ssd-plan-summary"><p><span>最大电流：</span>{current} mA</p><p><span>相对延迟：</span>{delay} ms</p><p><span>点位：</span><b>{points.A}·A</b><b>{points.C}·C</b></p></div><div className="ssd-divider"/><h2>训练设置</h2><div className="ssd-training-settings"><label><span>语料库</span><Choice label="语料库" value={corpus} options={['MSP']} onChange={setCorpus} disabled={run!=='ready'||active}/></label><label><span>句表号</span><Choice label="句表号" value={sentenceList} options={['1','2','3']} onChange={v=>{setSentenceList(v);resetTrial(0);setRecords([]);}} disabled={run!=='ready'||active||records.length>0}/></label><label><span>配置</span><Choice label="测试模式" value={testMode} options={['固定语速测试（安静）','自适应语速测试（安静）','自适应信噪比测试','固定信噪比测试','固定RIR测试']} onChange={setTestMode} disabled={run!=='ready'||active}/></label><label><span>配置</span><Choice label="语音形式" value={speechType} options={['原始语音','声码器仿真']} onChange={setSpeechType} disabled={run!=='ready'||active}/></label><label><span>语速自<br/>定义</span><div className="ssd-number"><input aria-label="自定义语速" type="number" min="0" max="6" step=".5" value={rate} disabled={run!=='ready'||active} onChange={e=>setRate(Math.max(0,Math.min(6,Number(e.target.value))))}/><small>字/S</small></div></label></div>
        <div className={`ssd-current-phase ${active?'active':''}`} role="status">{run!=='ready'&&<><i/>{phase}</>}</div>
        <div className="ssd-run-buttons">{active?<Button tone="danger" icon="action" onClick={stop}>紧急停止</Button>:run==='ready'||run==='aborted'?<Button tone="success-button" disabled={!audioReady} onClick={()=>start(false)}>{!audioReady?'正在准备题目音频…':run==='aborted'?'重新执行本句':'开始播放音频并刺激'}<span>▶</span></Button>:<><div><Button tone="primary" disabled={run!=='saved'} onClick={()=>trial===19?finish():resetTrial(trial+1)}>{trial===19?'查看实验结果':'下一句'}</Button><Button tone="primary" icon="action" disabled={!scoring||!allScored(scores)} onClick={save}>{run==='saved'?'本句已保存':'保存本句结果'}</Button></div><Button tone="danger" icon="action" onClick={()=>setDialog('end')}>结束实验</Button></>}{(run==='ready'&&records.length>0||run==='aborted')&&<Button onClick={()=>setDialog('end')}>结束实验</Button>}</div>
      </aside>
      <audio ref={audioRef} src={asset(`sentence-${trial+1}.wav`)} preload="auto" onLoadedMetadata={e=>{setDuration(e.target.duration);setAudioReady(true);}} onCanPlay={()=>setAudioReady(true)} onEnded={audioEnded} onError={()=>{setAudioReady(false);flash('题目音频加载失败，请刷新页面');}}/>
    </>}
    {page==='results'&&<section className="ssd-panel ssd-results"><div className="ssd-section-heading"><div><h1>实验结果</h1><p>包络-tACS 单刺激言语训练 · 实验已结束</p></div><Button tone="primary" onClick={exportResults}>导出实验结果</Button></div><div className="ssd-result-stats"><div>已完成句数<b>{records.length}<small> / 20</small></b></div><div>已评分字数<b>{records.reduce((s,r)=>s+r.total,0)}</b></div><div>正确字数<b>{records.reduce((s,r)=>s+r.correct,0)}</b></div><div>总正确率<b>{records.length?`${Math.round(records.reduce((s,r)=>s+r.correct,0)/records.reduce((s,r)=>s+r.total,0)*100)}%`:'—'}</b></div></div><table><thead><tr><th>句次</th><th>题目</th><th>正确 / 总字数</th><th>正确率</th><th>状态</th><th>操作</th></tr></thead><tbody>{records.map(r=><tr key={r.trial}><td>{String(r.trial).padStart(2,'0')}</td><td>{r.sentence}</td><td>{r.correct} / {r.total}</td><td>{Math.round(r.correct/r.total*100)}%</td><td className="success">已保存</td><td><Button onClick={()=>setInspectRecord(r)}>查看逐字结果</Button></td></tr>)}</tbody></table>{!records.length&&<p className="ssd-no-results">本次实验没有已保存的评分结果。</p>}<footer><Button onClick={()=>{setPoints({A:null,C:null});invalidate();setRecords([]);resetTrial(0);setPage('plan');}}>返回刺激配置，重新演示</Button></footer></section>}
    {notice&&<div className="ssd-toast" role="status">{notice}</div>}
    <button className="ssd-demo-trigger" onClick={()=>setHelpVisible(true)}>演示说明</button>
    {dialog==='confirm'&&<Dialog title="确认实验配置" onClose={()=>setDialog(null)} footer={<><Button onClick={()=>setDialog(null)}>返回调整</Button><Button tone="primary" onClick={()=>{setDialog(null);setPage('training');}}>确认并进入言语训练</Button></>}><div className="ssd-confirm-info"><p><span>刺激范式</span><b>包络-tACS · 单刺激</b></p><p><span>最大电流</span><b>{current} mA</b></p><p><span>刺激启动延时</span><b>{delay} ms（{Number(delay)/1000} s）</b></p>{['A','C'].map(r=><p key={r}><span>{r==='A'?'阳极':'阴极'}</span><b>{points[r]}·{r} / {channels[r]}</b><em className="success">阻抗检测通过</em></p>)}</div><p className="ssd-modal-note">音频从 0 s 开始，刺激在音频开始 {Number(delay)/1000} s 后启动；完整播放结束后再进行逐字评分。</p></Dialog>}
    {dialog==='end'&&<Dialog title="结束本次实验？" onClose={()=>setDialog(null)} footer={<><Button onClick={()=>setDialog(null)}>继续训练</Button><Button tone="danger" onClick={finish}>确认结束</Button></>}><p>已保存 {records.length} 句结果。{run==='scoring'?'当前句尚未保存，结束后不计入结果。':'结束后可查看并导出实验结果。'}</p></Dialog>}
    {dialog==='back'&&<Dialog title="返回电极配置？" onClose={()=>setDialog(null)} footer={<><Button onClick={()=>setDialog(null)}>继续训练</Button><Button tone="primary" onClick={()=>{setDialog(null);setPage('electrodes');resetTrial(0);setRecords([]);}}>返回电极配置</Button></>}><p>本次训练进度将重新开始，已保存到浏览器的结果仍可在演示说明中恢复。</p></Dialog>}
    {dialog==='leave'&&<Dialog title="单刺激流程演示" onClose={()=>setDialog(null)} footer={<><Button onClick={()=>setDialog(null)}>继续演示</Button>{onHome&&<Button tone="primary" onClick={onHome}>返回项目首页</Button>}</>}><p>当前是本组 Figma 页面的起点。设置电流与延时后，点击“进入电极配置与检测”开始演示。</p></Dialog>}
    {helpVisible&&<Dialog title="演示说明" onClose={()=>setHelpVisible(false)} footer={<Button tone="primary" onClick={()=>setHelpVisible(false)}>开始演示</Button>}><p>按照 Figma 的 5 个页面串联：参数配置 → 阳极/阴极点位 → 阻抗检测 → 播放与刺激 → 逐字评分。</p><ol><li>选择阳极点位后，切换阴极并选择另一个点位。</li><li>点击检测，等待通过，再确认实验配置。</li><li>播放结束后点击每个字下的对错，或使用全对 / 全错。</li><li>保存本句后进入下一句，结束实验可查看与导出结果。</li></ol><p className="ssd-modal-note">演示使用合成语音、Figma 波形示意和模拟阻抗；训练设置用于展示选项与锁定流程，未实现声码器、噪声和自适应算法。没有真实刺激输出。</p><label className="ssd-fail-option"><input type="checkbox" checked={failNext} onChange={e=>setFailNext(e.target.checked)}/>下次阻抗检测演示“不通过”</label><Button onClick={()=>{try{const cached=JSON.parse(localStorage.getItem(STORAGE_KEY)||'[]');if(!Array.isArray(cached)||!cached.length){flash('暂无已保存结果');return;}stopPlayback();setRecords(cached);setPage('results');setHelpVisible(false);}catch{flash('无法读取已保存结果');}}}>查看上次保存的演示结果</Button></Dialog>}
    {inspectRecord&&<Dialog title={`第 ${inspectRecord.trial} 句评分结果`} onClose={()=>setInspectRecord(null)} footer={<Button tone="primary" onClick={()=>setInspectRecord(null)}>关闭</Button>}><div className="ssd-inspect-words">{Array.from(inspectRecord.sentence).map((word,i)=><span key={i} className={inspectRecord.scores[i]?'success':'ssd-error'}>{word}<small>{inspectRecord.scores[i]?'正确':'错误'}</small></span>)}</div><p>电流 {inspectRecord.current} mA · 延时 {inspectRecord.delay} ms · {inspectRecord.points.A}·A / {inspectRecord.points.C}·C</p></Dialog>}
  </main></div>;
}
