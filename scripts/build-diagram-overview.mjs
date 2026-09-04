import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = path.dirname(fileURLToPath(import.meta.url));
const rootDir = path.resolve(scriptDir, '..');
const outputPath = path.join(rootDir, 'eeg-tes-rehab-overview.html');
const deployDirectory = path.join(rootDir, 'public', 'eeg-rehab');

const documents = {
  course: 'eeg-tes-course.workflow.html',
  agent: 'eeg-agent-internal.architecture.html',
  analysis: 'eeg-agent-analysis.workflow.html',
  integration: 'eeg-rehab-integration.workflow.html',
};

const documentUrls = JSON.stringify(documents);

const html = `<!doctype html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>EEG-tES 康复与 Agent 图谱总览</title>
  <style>
    :root {
      color-scheme: light;
      --bg: #f3f6fa;
      --surface: rgba(255,255,255,.92);
      --line: #d9e2ec;
      --text: #132238;
      --muted: #617086;
      --accent: #0891b2;
      --accent-soft: #e6f7fb;
      --shadow: 0 14px 38px rgba(31, 48, 73, .10);
    }
    * { box-sizing: border-box; }
    html, body { height: 100%; margin: 0; overflow: hidden; }
    body {
      font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      background:
        radial-gradient(circle at 15% -20%, rgba(8,145,178,.12), transparent 34%),
        radial-gradient(circle at 85% 0%, rgba(99,102,241,.09), transparent 30%),
        var(--bg);
      color: var(--text);
    }
    .app { height: 100%; display: grid; grid-template-rows: auto minmax(0,1fr); }
    header {
      min-height: 78px;
      padding: 14px 20px;
      display: flex;
      align-items: center;
      gap: 18px;
      border-bottom: 1px solid rgba(217,226,236,.9);
      background: var(--surface);
      backdrop-filter: blur(16px);
    }
    .brand { min-width: 330px; }
    h1 { margin: 0; font-size: 18px; letter-spacing: .01em; }
    .subtitle { margin-top: 5px; color: var(--muted); font-size: 12px; }
    .switcher {
      display: inline-flex;
      gap: 4px;
      padding: 4px;
      border: 1px solid var(--line);
      border-radius: 12px;
      background: #eef3f7;
    }
    .switcher button, .open-button {
      appearance: none;
      border: 0;
      border-radius: 9px;
      font: inherit;
      font-size: 13px;
      font-weight: 650;
      color: var(--muted);
      background: transparent;
      cursor: pointer;
      padding: 9px 14px;
      transition: .18s ease;
    }
    .switcher button:hover { color: var(--text); }
    .switcher button[aria-selected="true"] {
      color: #075985;
      background: white;
      box-shadow: 0 2px 10px rgba(42,61,82,.12);
    }
    .hint { margin-left: auto; color: var(--muted); font-size: 12px; white-space: nowrap; }
    main { min-height: 0; padding: 14px; }
    .workspace { height: 100%; display: grid; gap: 12px; }
    .workspace.single { grid-template-columns: minmax(0,1fr); }
    .workspace.compare { grid-template-columns: repeat(2, minmax(0,1fr)); }
    .workspace.course-reading,
    .workspace.analysis-reading,
    .workspace.integration-reading { grid-template-columns: clamp(360px, 34vw, 520px) minmax(0,1fr); }
    .note-panel {
      min-width: 0;
      min-height: 0;
      overflow: auto;
      padding: 24px 25px 34px;
      border: 1px solid var(--line);
      border-radius: 15px;
      background: white;
      box-shadow: var(--shadow);
      scrollbar-gutter: stable;
    }
    .note-panel h2 { margin: 0 0 8px; font-size: 21px; line-height: 1.3; }
    .note-panel h3 {
      display: flex;
      align-items: center;
      gap: 9px;
      margin: 25px 0 9px;
      padding-top: 18px;
      border-top: 1px solid #e8eef4;
      font-size: 15px;
      line-height: 1.35;
    }
    .note-panel p, .note-panel li { font-size: 13px; line-height: 1.75; }
    .note-panel p { margin: 8px 0; color: #42546a; }
    .note-panel ul { margin: 8px 0 10px; padding-left: 20px; color: #42546a; }
    .note-panel li + li { margin-top: 3px; }
    .note-panel strong { color: #14263d; }
    .note-kicker { margin-bottom: 8px; color: var(--accent); font-size: 11px; font-weight: 800; letter-spacing: .12em; }
    .step {
      display: inline-grid;
      flex: 0 0 auto;
      place-items: center;
      width: 25px;
      height: 25px;
      border-radius: 8px;
      color: #075985;
      background: var(--accent-soft);
      font-size: 12px;
      font-weight: 850;
    }
    .callout {
      margin: 16px 0;
      padding: 13px 14px;
      border-left: 3px solid var(--accent);
      border-radius: 0 9px 9px 0;
      color: #164e63;
      background: #edfafd;
      font-size: 13px;
      line-height: 1.7;
    }
    .result-list {
      display: grid;
      gap: 7px;
      margin-top: 11px;
    }
    .result-item {
      padding: 9px 11px;
      border: 1px solid #e4eaf1;
      border-radius: 9px;
      color: #42546a;
      background: #f8fafc;
      font-size: 12px;
      line-height: 1.55;
    }
    .result-item b { color: #172a42; }
    .pane {
      min-width: 0;
      min-height: 0;
      display: grid;
      grid-template-rows: 42px minmax(0,1fr);
      overflow: hidden;
      border: 1px solid var(--line);
      border-radius: 15px;
      background: white;
      box-shadow: var(--shadow);
    }
    .pane-bar {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 0 12px 0 15px;
      border-bottom: 1px solid #e7edf3;
      background: #fbfcfe;
    }
    .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--accent); }
    .pane-title { font-size: 13px; font-weight: 720; }
    .pane-note { color: var(--muted); font-size: 11px; }
    .open-button {
      margin-left: auto;
      padding: 6px 9px;
      border: 1px solid var(--line);
      color: #365066;
      background: white;
      font-size: 11px;
    }
    iframe { width: 100%; height: 100%; border: 0; background: #f7f9fb; }
    @media (max-width: 980px) {
      header { align-items: flex-start; flex-wrap: wrap; }
      .brand { min-width: 0; width: 100%; }
      .hint { display: none; }
      main { padding: 8px; }
      .workspace.compare { grid-template-columns: 1fr; grid-template-rows: repeat(2, minmax(0,1fr)); }
      .workspace.course-reading,
      .workspace.analysis-reading,
      .workspace.integration-reading { grid-template-columns: 1fr; grid-template-rows: minmax(260px, .85fr) minmax(420px, 1.15fr); overflow: auto; }
    }
  </style>
</head>
<body>
  <div class="app">
    <header>
      <div class="brand">
        <h1>EEG-tES 康复与 Agent 图谱总览</h1>
        <div class="subtitle">一处查看完整疗程、脑机与康复联动、Agent 工具架构，以及从数据准入到医生复核的分析链路</div>
      </div>
      <div class="switcher" role="tablist" aria-label="图谱视图">
        <button type="button" role="tab" data-mode="course" aria-selected="true">完整疗程</button>
        <button type="button" role="tab" data-mode="agent" aria-selected="false">Agent 架构</button>
        <button type="button" role="tab" data-mode="analysis" aria-selected="false">分析链路</button>
        <button type="button" role="tab" data-mode="integration" aria-selected="false">康复联动</button>
        <button type="button" role="tab" data-mode="compare" aria-selected="false">双图对照</button>
      </div>
      <div class="hint">快捷键 1 / 2 / 3 / 4 / 5 切换视图</div>
    </header>
    <main>
      <div id="workspace" class="workspace single"></div>
    </main>
  </div>
  <template id="course-notes">
    <article class="note-panel">
      <div class="note-kicker">完整流程说明</div>
      <h2>从建立疗程到结疗复核</h2>
      <p>完整流程必须区分两个层次：<strong>Session</strong> 是一次治疗与测量，<strong>Course</strong> 是由多次Session组成的完整康复疗程。单次训练只能形成当次结果，疗效判断主要发生在多次疗程和结疗评估层面。</p>
      <div class="callout"><strong>核心目标：</strong>把平板训练、tES刺激、EEG测量和临床评估放入同一疗程，让每条数据都知道属于谁、哪一次治疗、采用什么协议，以及能否与前后记录比较。</div>

      <h3><span class="step">1</span>建立完整疗程</h3>
      <p>医生或治疗师先创建 Course ID，确定患者、康复目标、训练内容、刺激方案、EEG协议、临床量表和计划疗程次数。</p>
      <ul>
        <li>康复目标可包括命名、理解、表达等具体能力；</li>
        <li>指定哪些Session需要EEG，哪些只记录训练与刺激；</li>
        <li>保存方案版本，避免疗程中参数变化却无法追溯。</li>
      </ul>
      <p>每次治疗再创建一个 Session ID，全部设备数据都挂在这个Session下。</p>

      <h3><span class="step">2</span>开始一次 Session</h3>
      <p>治疗师在上位机选择患者和本次治疗。系统加载训练任务、刺激计划、电极配置、既往耐受记录，以及本次是否需要训练前后EEG。</p>
      <p>开始前检查脑机设备、平板、患者身份和方案版本，防止平板数据与脑机数据归属错误。</p>

      <h3><span class="step">3</span>训练前 EEG</h3>
      <ul>
        <li>戴好电极并完成阻抗检测；</li>
        <li>按固定范式和时长进行采集；</li>
        <li>采集后检查掉线、坏通道、伪迹和有效时长；</li>
        <li>保存原始数据、质控结果、通道配置和处理版本。</li>
      </ul>
      <p><strong>阻抗通过不等于EEG最终可用。</strong>阻抗是采集前的电极接触检查，信号质控是采集后的数据可用性判断。</p>

      <h3><span class="step">4</span>统一开始平板训练与 tES</h3>
      <p>治疗师从上位机统一启动本次治疗。上位机向平板发送Session ID和开始指令，同时启动或记录tES设备状态。</p>
      <ul>
        <li>上位机作为统一时间基准；</li>
        <li>开始、暂停、恢复、中断和结束均写入同一Session；</li>
        <li>连接可采用局域网API/WebSocket、设备SDK、串口或蓝牙协议；</li>
        <li>无需治疗结束后人工比对各设备时间。</li>
      </ul>

      <h3><span class="step">5</span>治疗过程中记录什么</h3>
      <p><strong>平板：</strong>题型、难度、题目出现、回答、正确性、提示、反应时间和异常退出。</p>
      <p><strong>tES：</strong>实际强度、持续时间、电极位置、阻抗、中断、报警、患者耐受和不良反应。</p>
      <p><strong>上位机：</strong>设备在线状态、统一事件、操作者行为、方案变更和异常处理。</p>
      <div class="callout"><strong>当前产品边界：</strong>如果治疗过程中不采EEG，图片出现和患者回答的时间只能用于行为过程分析；tES时间只证明刺激是否与训练同步执行，不能据此分析某道题对应的脑电波形变化。</div>

      <h3><span class="step">6</span>治疗结束与执行核对</h3>
      <p>系统核对平板任务是否完成、实际训练时长、tES是否完整执行、是否中断或报警，以及患者是否出现不适。</p>
      <p>这一阶段形成的是<strong>治疗执行证据</strong>，说明治疗有没有按计划实施，还不能单独证明治疗有效。</p>

      <h3><span class="step">7</span>训练后 EEG</h3>
      <p>如果方案要求Post EEG，应尽量与Pre EEG保持同一协议、设备、电极配置、参考方式、采集时长和患者状态，并再次进行信号质控。</p>
      <p>训练后EEG失败不代表治疗失败：行为结果和刺激日志仍然有效，但EEG前后比较需要禁用或降低可信度。</p>

      <h3><span class="step">8</span>单次 Session 分析</h3>
      <ul>
        <li><strong>治疗执行：</strong>本次训练与刺激是否按方案完成；</li>
        <li><strong>平板表现：</strong>正确率、独立答对率、提示和反应时间；</li>
        <li><strong>EEG辅助变化：</strong>在质量和可比性通过后，分析预设指标。</li>
      </ul>
      <p>单次报告应明确区分“行为改善”“刺激完整执行”和“EEG出现变化”，不能把三者混为同一个疗效结论。</p>

      <h3><span class="step">9</span>进入下一次治疗</h3>
      <p>下次Session开始前，系统展示上次完成情况、训练表现、刺激耐受、EEG质量问题和待处理异常。</p>
      <p>Agent可以提示检查电极、建议补采或提醒医生复核，但初期不应自动修改刺激参数和治疗方案。</p>

      <h3><span class="step">10</span>多次疗程趋势</h3>
      <p>随着Session增加，系统比较独立答对率、提示依赖、反应时间、题型表现、刺激完成率和EEG变化方向。</p>
      <p>这一层用于判断变化是否持续、是否只是偶然波动，以及行为结果和EEG辅助证据是否一致。单次异常不应轻易改变整体判断。</p>

      <h3><span class="step">11</span>结疗评估</h3>
      <p>完成计划疗程后，综合临床量表、平板总体趋势、刺激完成率、EEG阶段变化、不良事件和医生观察。</p>
      <p>证据顺序应保持为：<strong>临床评估 → 行为表现 → 刺激执行 → EEG辅助变化 → Agent综合解释</strong>。</p>

      <h3><span class="step">12</span>两种 EEG 采集模式</h3>
      <p><strong>研究型密集采集：</strong>每次治疗前后采集，数据丰富，但耗时、患者负担和伪迹风险更高。</p>
      <p><strong>临床型阶段采集：</strong>基线、中期和结疗时采集；每次Session仍记录训练与刺激，更适合常规落地。</p>
      <p>产品不应把“每次必须Pre/Post EEG”写死，而应由疗程协议配置采集节点，并允许医生临时追加。</p>

      <h3><span class="step">✓</span>完整流程的结果</h3>
      <div class="result-list">
        <div class="result-item"><b>每次Session：</b>留下完整、同步、可追溯的训练、刺激和测量记录。</div>
        <div class="result-item"><b>多次Session：</b>逐渐形成行为趋势、刺激依从性和EEG辅助趋势。</div>
        <div class="result-item"><b>疗程结束：</b>由Agent生成证据化草稿，再由医生完成最终复核。</div>
      </div>
      <div class="callout"><strong>最终目的：</strong>不是让每次训练都强行产生“疗效结论”，而是让每一次可靠记录逐渐组成完整、可信、可追溯的疗程证据。</div>
    </article>
  </template>
  <template id="analysis-notes">
    <article class="note-panel">
      <div class="note-kicker">详细解读</div>
      <h2>疗程后分析 Agent 如何工作</h2>
      <p>这条链路不是把全部数据扔给大模型直接生成结论，而是逐级收紧证据：先确认疗程真的完成，再确认数据能够使用，最后确认训练前后可以比较。</p>
      <div class="callout"><strong>核心原则：</strong>平板训练与临床评估回答“功能有没有改善”；刺激记录回答“治疗有没有按方案执行”；EEG回答“是否伴随可信的生理变化”。</div>

      <h3><span class="step">1</span>疗程完整性检查</h3>
      <p><strong>回答：</strong>本次治疗与测量是否按计划完成。</p>
      <ul>
        <li>患者、疗程编号和 Session ID 是否一致；</li>
        <li>训练前 EEG、平板训练、tES、训练后 EEG 是否都有记录；</li>
        <li>协议、设备、电极配置和软件版本是否保存；</li>
        <li>是否存在中途退出、异常中断或关键字段缺失。</li>
      </ul>
      <p><strong>输出：</strong>完整、部分完整或不完整。缺失训练后 EEG 时，仍可分析训练与刺激，但不能生成 EEG 前后变化结论。</p>

      <h3><span class="step">2</span>数据质控</h3>
      <p><strong>回答：</strong>记录存在，但数据是否真的可用。</p>
      <ul>
        <li>平板：重复提交、异常反应时间、题目难度和提示记录；</li>
        <li>刺激：实际强度、持续时间、中断、阻抗、报警与耐受；</li>
        <li>EEG：坏通道、掉线、饱和、工频、眼动肌电伪迹和有效时长。</li>
      </ul>
      <p>阻抗检测只代表<strong>采集前接触状态</strong>；采集完成后仍需离线信号质控。输出应包含可用、限制使用、不可用，以及具体原因和补采建议。</p>

      <h3><span class="step">3</span>行为变化分析</h3>
      <p><strong>回答：</strong>患者在康复任务中的功能表现有没有改善。</p>
      <ul>
        <li>正确率、独立答对率、提示后答对率；</li>
        <li>提示次数、反应时间、完成题量；</li>
        <li>不同题型、难度和错误类型的变化；</li>
        <li>是否存在练习效应、难度下降或后半程疲劳。</li>
      </ul>
      <p>不能只看总正确率。例如正确率提高但题目变简单，不应直接解释为能力改善；正确率稳定但提示依赖下降，也可能是有价值的进步。</p>

      <h3><span class="step">4</span>EEG 可比性判断</h3>
      <p><strong>回答：</strong>训练前和训练后两次 EEG 能否放在一起比较。</p>
      <ul>
        <li>范式、采集时长、采样率、参考方式是否一致；</li>
        <li>电极配置和共同有效通道是否足够；</li>
        <li>两次信号质量差异是否过大；</li>
        <li>睁闭眼、疲劳、用药和采集环境是否相近；</li>
        <li>是否使用同一预处理算法和参数版本。</li>
      </ul>
      <p>建议输出高、中、低或不可比。这里的“可信度”描述的是<strong>比较条件</strong>，不是治疗有效程度。</p>

      <h3><span class="step">5</span>EEG 前后变化分析</h3>
      <p><strong>回答：</strong>通过可比性闸门后，是否出现预先定义的脑电指标变化。</p>
      <ul>
        <li>指定脑区的频带功率与左右差异；</li>
        <li>脑区连接、稳定性或特定 ERP 指标；</li>
        <li>变化方向、幅度、共同通道和数据可信度。</li>
      </ul>
      <p>Agent不能临时挑选“看起来变化最大”的指标，也不能把“α功率变化”直接改写成“语言脑区恢复”。EEG只能作为辅助生理证据。</p>

      <h3><span class="step">6</span>多次趋势与证据融合</h3>
      <p><strong>回答：</strong>变化是持续趋势，还是某一次偶然波动。</p>
      <ul>
        <li>行为改善是否在多个疗程中重复出现；</li>
        <li>EEG变化的方向是否稳定；</li>
        <li>行为和EEG是否一致，还是相互冲突；</li>
        <li>刺激中断、低质量采集等异常是否能够解释波动。</li>
      </ul>
      <p>单次前后差异容易受到睡眠、疲劳、药物和电极接触影响。多次记录的价值，是逐渐提高结论稳定性，而不是简单追求每次指标都变好。</p>

      <h3><span class="step">7</span>辅助报告草稿</h3>
      <p>报告按照医生的阅读顺序组织：</p>
      <ul>
        <li>一句话结论；</li>
        <li>疗程执行与异常情况；</li>
        <li>行为变化与难度校正；</li>
        <li>EEG质量、可比性和前后变化；</li>
        <li>证据一致性、局限与下次建议。</li>
      </ul>
      <p>报告必须先说明数据质量和可比性，再展示EEG变化，不能把一组数值直接包装成疗效结论。</p>

      <h3><span class="step">8</span>医生复核并锁定</h3>
      <p>Agent生成的是草稿，不是最终临床结论。医生可以接受、修改、删除、退回重分析或补充临床观察。</p>
      <p>系统应保存Agent原始输出、调用工具及版本、计算参数、医生修改内容、最终结论、复核人和时间，形成完整审计记录。</p>

      <h3><span class="step">✓</span>最终允许出现的结果</h3>
      <div class="result-list">
        <div class="result-item"><b>完整结论：</b>行为结果有效，EEG前后可比，可以联合解释。</div>
        <div class="result-item"><b>部分结论：</b>行为结果有效，但EEG不可比或不可用，只报告训练变化。</div>
        <div class="result-item"><b>低可信结论：</b>允许有限比较，但必须说明坏通道、伪迹或协议差异。</div>
        <div class="result-item"><b>停止分析：</b>关键记录缺失或数据质量不足，建议核查或补采。</div>
      </div>
      <div class="callout"><strong>Agent最重要的能力：</strong>不是每次都给出答案，而是知道什么时候可以回答、回答到什么程度，以及什么时候必须停止解释。</div>
    </article>
  </template>
  <template id="integration-notes">
    <article class="note-panel">
      <div class="note-kicker">产品联动说明</div>
      <h2>脑机如何进入康复训练流程</h2>
      <p>这张图不展开电极、阻抗或刺激参数配置，只说明<strong>平板训练、tES刺激、EEG测量和Agent分析如何组成一条产品流程</strong>。重点不是让脑机替代训练，而是让训练执行、刺激执行和脑状态测量都归属于同一个疗程。</p>
      <div class="callout"><strong>三个角色不能混淆：</strong>平板训练回答“患者做得怎么样”；刺激记录回答“治疗是否按方案执行”；EEG回答“是否观察到可比较的辅助生理变化”。最终疗效仍以临床评估和训练表现为主。</div>

      <h3><span class="step">1</span>建立疗程</h3>
      <p>医生先定义一个 Course：患者是谁、主要康复目标是什么、计划训练多少次、是否结合tES，以及在哪些节点测量EEG。</p>
      <ul>
        <li>训练目标可按命名、听理解、阅读或表达能力拆分；</li>
        <li>EEG可以安排在基线、中期、结疗，也可以按研究方案每次测量；</li>
        <li>保存训练、刺激和测量协议版本，确保后续结果可追溯。</li>
      </ul>
      <p>这一层解决的是<strong>整段疗程怎么设计</strong>，不是某一次操作怎么点击。</p>

      <h3><span class="step">2</span>配置脑机介入节点</h3>
      <p>产品不应把所有Session写死为“训练前EEG—训练—训练后EEG”。应允许医生按目的选择组合：</p>
      <ul>
        <li><strong>训练为主：</strong>只做平板训练，记录行为结果；</li>
        <li><strong>训练＋刺激：</strong>平板与tES同步执行，记录刺激日志；</li>
        <li><strong>EEG＋训练：</strong>在阶段节点增加脑状态测量；</li>
        <li><strong>EEG＋训练＋刺激：</strong>形成最完整的辅助证据链。</li>
      </ul>
      <p>这使EEG成为可配置的测量手段，而不是每次治疗的强制负担。</p>

      <h3><span class="step">3</span>创建同一个 Session</h3>
      <p>每次治疗只创建一个 Session ID，上位机把它发送给平板、EEG/tES设备和Agent。各系统返回的数据都必须带回同一个ID。</p>
      <ul>
        <li>统一患者、疗程、当次治疗和方案版本；</li>
        <li>统一开始、暂停、恢复、中断和结束状态；</li>
        <li>各设备保留本地时间，同时记录与上位机的时间偏移。</li>
      </ul>
      <div class="callout"><strong>产品价值：</strong>不再靠治疗结束后手工对时间。Session ID解决数据归属，统一事件和时钟校正解决时间对齐。</div>

      <h3><span class="step">4</span>训练前 EEG（可选）</h3>
      <p>如果本次安排Pre EEG，系统执行固定协议采集并立即质控。输出不只是“有文件”，而是：</p>
      <ul>
        <li><strong>可用：</strong>满足预设质量要求，可以进入后续比较；</li>
        <li><strong>限制使用：</strong>存在局部坏通道或伪迹，只允许有限分析；</li>
        <li><strong>不可用：</strong>质量不足，建议重采或取消本次EEG比较；</li>
        <li><strong>未安排：</strong>本次按方案不测量EEG，不属于缺失。</li>
      </ul>
      <p>即使Pre EEG不可用，训练仍可以继续；系统只是关闭本次EEG前后比较能力。</p>

      <h3><span class="step">5</span>平板训练与 tES 同步执行</h3>
      <p>治疗师在上位机点击开始后，系统分别向平板与刺激设备发指令，并等待设备返回“已实际开始”，而不是仅记录按钮点击时间。</p>
      <ul>
        <li>平板记录题目、作答、正确性、提示、反应时间与退出；</li>
        <li>tES记录实际强度、持续时间、中断、报警、阻抗与耐受；</li>
        <li>上位机维护统一的Session状态和异常处理记录。</li>
      </ul>
      <p><strong>当前产品边界：</strong>治疗中不采EEG，因此图片出现、患者回答等事件用于解释训练过程；它们不会被写成“某个事件引起某段EEG波形变化”。</p>

      <h3><span class="step">6</span>汇总训练与刺激结果</h3>
      <p>训练结束后，先分别形成两份确定性结果：</p>
      <div class="result-list">
        <div class="result-item"><b>平板结果：</b>完成量、正确率、独立答对率、提示依赖、反应时间、题型和难度。</div>
        <div class="result-item"><b>刺激结果：</b>计划与实际参数、有效刺激时长、中断、报警、耐受及不良事件。</div>
      </div>
      <p>刺激完整执行只说明方案落实良好，<strong>不能直接等于治疗有效</strong>。</p>

      <h3><span class="step">7</span>训练后 EEG（可选）</h3>
      <p>如果本次安排Post EEG，应尽量复用Pre EEG的协议、设备、电极配置、参考方式、时长和患者状态，再做同样的质量检查。</p>
      <p>只有Pre与Post都可用，并通过协议一致性和共同有效通道检查，Agent才允许进行前后变化分析。Post EEG失败不会抹掉平板和刺激结果。</p>

      <h3><span class="step">8</span>生成单次康复结果</h3>
      <p>系统把本次Session结果按三个层级并列展示，而不是合成一个含义模糊的“脑电总分”：</p>
      <div class="result-list">
        <div class="result-item"><b>康复表现：</b>患者在训练任务中的功能表现和变化。</div>
        <div class="result-item"><b>治疗执行：</b>刺激和训练是否按方案完成，有无异常。</div>
        <div class="result-item"><b>EEG辅助观察：</b>质量、可比性、预设指标变化及可信限制。</div>
      </div>
      <p>如果本次没有EEG，报告仍然成立，只显示前两层，并明确“本次未安排EEG测量”。</p>

      <h3><span class="step">9</span>累计多次趋势</h3>
      <p>单次Session反映当次表现，多次Session才能逐渐形成疗程趋势：</p>
      <ul>
        <li>行为：正确率、独立作答、提示依赖、反应时间是否持续改善；</li>
        <li>执行：刺激完成率、异常中断和耐受是否稳定；</li>
        <li>EEG：在相同协议下，预设指标的方向是否可重复；</li>
        <li>一致性：行为改善是否伴随EEG辅助变化，还是证据相互冲突。</li>
      </ul>
      <p>Agent应标出趋势和异常来源，但不能把一次偶然波动解释成康复效果。</p>

      <h3><span class="step">10</span>医生决定下一步</h3>
      <p>医生结合临床评估、训练表现、刺激执行、EEG辅助证据和患者状态，决定继续原方案、调整训练、追加测量或结束疗程。</p>
      <p>Agent可以生成草稿、提示风险和推荐复核项，但初期不应自动修改刺激参数，也不能替代医生作出疗效判断。</p>

      <h3><span class="step">✓</span>产品允许形成的四类结果</h3>
      <div class="result-list">
        <div class="result-item"><b>完整结果：</b>行为和刺激记录完整，前后EEG质量与可比性通过，可联合展示。</div>
        <div class="result-item"><b>部分结果：</b>未安排EEG，或只安排单次EEG；正常展示训练与刺激结果。</div>
        <div class="result-item"><b>降级结果：</b>EEG质量有限；保留可用信息并显著标注限制，不强行解释。</div>
        <div class="result-item"><b>异常结果：</b>刺激中断、设备报警或不良事件；单独标记并进入医生复核。</div>
      </div>
      <div class="callout"><strong>脑机在这个产品里的实际作用：</strong>提供标准化测量、完整执行追踪和可复核的辅助趋势；训练结果仍是主体，EEG增加的是证据维度与解释边界。</div>
    </article>
  </template>
  <script>
    const documents = ${documentUrls};
    const metadata = {
      course: { title: '完整疗程流程', note: '以训练结果为主、EEG 为辅助的疗程证据链' },
      agent: { title: 'EEG Agent 内部架构', note: 'Agent 编排、确定性工具和临床解释边界' },
      analysis: { title: '疗程后分析链路', note: '三个证据闸门、行为主证据、EEG 辅助证据与医生复核' },
      integration: { title: '脑机与康复联动', note: '同一 Session、选择性 EEG 测量、训练与刺激执行、疗程反馈' }
    };
    const workspace = document.getElementById('workspace');
    const buttons = [...document.querySelectorAll('[data-mode]')];

    function createAnalysisNotes() {
      return document.getElementById('analysis-notes').content.cloneNode(true);
    }

    function createCourseNotes() {
      return document.getElementById('course-notes').content.cloneNode(true);
    }

    function createIntegrationNotes() {
      return document.getElementById('integration-notes').content.cloneNode(true);
    }

    function openStandalone(key) {
      window.open(documents[key], '_blank', 'noopener,noreferrer');
    }

    function createPane(key) {
      const pane = document.createElement('section');
      pane.className = 'pane';
      pane.innerHTML = '<div class="pane-bar"><span class="dot"></span><span class="pane-title"></span><span class="pane-note"></span><button class="open-button" type="button">单独打开 ↗</button></div>';
      pane.querySelector('.pane-title').textContent = metadata[key].title;
      pane.querySelector('.pane-note').textContent = metadata[key].note;
      pane.querySelector('.open-button').addEventListener('click', () => openStandalone(key));
      const frame = document.createElement('iframe');
      frame.title = metadata[key].title;
      frame.setAttribute('sandbox', 'allow-scripts allow-same-origin allow-downloads allow-modals');
      frame.src = documents[key];
      pane.appendChild(frame);
      return pane;
    }

    function render(mode) {
      workspace.replaceChildren();
      if (mode === 'course' || mode === 'analysis' || mode === 'integration') {
        workspace.className = 'workspace ' + mode + '-reading';
        const notes = mode === 'course'
          ? createCourseNotes()
          : mode === 'analysis'
            ? createAnalysisNotes()
            : createIntegrationNotes();
        workspace.appendChild(notes);
        workspace.appendChild(createPane(mode));
      } else {
        workspace.className = 'workspace ' + (mode === 'compare' ? 'compare' : 'single');
        const keys = mode === 'compare' ? ['course', 'agent'] : [mode];
        keys.forEach(key => workspace.appendChild(createPane(key)));
      }
      buttons.forEach(button => button.setAttribute('aria-selected', String(button.dataset.mode === mode)));
      history.replaceState(null, '', '#' + mode);
    }

    buttons.forEach(button => button.addEventListener('click', () => render(button.dataset.mode)));
    window.addEventListener('keydown', event => {
      if (event.key === '1') render('course');
      if (event.key === '2') render('agent');
      if (event.key === '3') render('analysis');
      if (event.key === '4') render('integration');
      if (event.key === '5') render('compare');
    });
    const initialMode = ['course', 'agent', 'analysis', 'integration', 'compare'].includes(location.hash.slice(1)) ? location.hash.slice(1) : 'course';
    render(initialMode);
  </script>
</body>
</html>`;

fs.writeFileSync(outputPath, html);
fs.mkdirSync(deployDirectory, { recursive: true });
fs.writeFileSync(path.join(deployDirectory, path.basename(outputPath)), html);
for (const documentPath of Object.values(documents)) {
  fs.copyFileSync(path.join(rootDir, documentPath), path.join(deployDirectory, documentPath));
}
console.log(outputPath);
