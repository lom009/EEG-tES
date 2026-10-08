# REVE EEG 基础模型可行性验证与产品化 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 用一套可审计、可复现的离线验证流程，判断 REVE 是否能为 EEG-tES 康复产品提供稳定的 EEG 质控、状态表征与训练前后辅助变化证据。

**Architecture:** REVE 只作为 EEG 表征编码器，不直接判断疗效。系统先完成脱敏、统一采集检查、固定预处理和数据质控，再同时运行 REVE 与 EEGNet 基线，按患者隔离评估；通过验证后，结构化结果才能进入 Agent 报告草稿并由医生复核。

**Tech Stack:** Python 3.11、PyTorch 2.6+、Transformers 4.56.2、MNE、scikit-learn、REVE-base、EEGNet、MLflow 或等价实验记录工具、现有 Course/Session 数据模型。

---

## 0. 文档信息

| 项目 | 内容 |
|---|---|
| 文档性质 | 立项评审稿 / 技术可行性验证计划 |
| 适用对象 | 公司管理层、产品、算法、临床、数据、研发与合规团队 |
| 计划周期 | 8 周离线 PoC；产品化与临床验证另行立项 |
| 当前产品定位 | 康复训练结果为主要证据，EEG 为辅助生理证据 |
| 拟验证模型 | REVE-base，约 6920 万参数 |
| 对照模型 | EEGNet；必要时增加 CBraMod 作为轻量模型对照 |
| 部署方式 | 第一阶段仅限本地或机构内网离线运行 |
| 最终决策 | 继续产品化、继续收集数据、调整任务，或停止投入 |

## 1. 给管理层的执行摘要

### 1.1 为什么现在值得验证

现有产品已经能够形成训练行为结果、刺激执行记录和训练前后 EEG，但 EEG 的产品价值仍停留在“采集到了数据”，尚未形成稳定的辅助证据能力。传统算法能够计算频段功率等固定指标，却难以覆盖设备、通道、患者和任务差异。REVE 是面向多种 EEG 设置预训练的基础编码器，可以先把不同 EEG 片段转换成统一表征，再针对我们的任务训练小型模型。

REVE 官方代码采用 MIT License；预训练权重允许研究、商业和个人用途，但需在 Hugging Face 申请访问并遵守隐私、安全和禁止擅自再分发权重等条件：

- 官方代码：https://github.com/elouayas/reve_eeg
- 官方权重：https://huggingface.co/brain-bzh/reve-base
- 论文：https://arxiv.org/abs/2510.21585
- Brain4FMs 横向评测：https://arxiv.org/abs/2602.11558

### 1.2 本项目解决什么

本项目只回答四个问题：

1. 我们现有设备采集的数据能否稳定输入 REVE；
2. REVE 是否比 EEGNet 或传统指标提供更多可复现信息；
3. REVE 能否辅助识别数据质量、脑状态差异和患者内前后变化；
4. 上述结果是否能够以可审计、不过度解释的方式进入康复报告。

### 1.3 本项目不承诺什么

- 不承诺 REVE 可以直接判断治疗有效或无效；
- 不将训练前后 EEG 差异直接解释为神经功能恢复；
- 不用模型自动调整 tES 参数；
- 不把一次小样本实验当成临床证据；
- 不在 PoC 阶段接入实时设备安全控制；
- 不用片段随机划分制造虚高指标；
- 不允许大语言模型绕过确定性分析直接解释原始 EEG。

### 1.4 建议管理层批准的事项

建议批准一个有明确停止条件的 8 周 PoC：

- 授权申请 REVE 权重并完成许可证、权重协议和安全审查；
- 授权使用脱敏后的历史 EEG、Session 与训练结果；
- 指定临床负责人制定质控标签和结果解释边界；
- 提供一名算法工程师、一名兼职数据工程师、一名兼职临床专家；
- 提供本地 NVIDIA GPU 或等价云端计算资源，但患者数据不得离开批准环境；
- 第 3、6、8 周设置决策门，不满足条件则停止扩大投入。

## 2. 产品定位与业务价值

### 2.1 产品证据层级

| 证据来源 | 产品角色 | 回答的问题 |
|---|---|---|
| 康复训练与临床量表 | 主要疗效证据 | 患者功能表现是否改善 |
| 刺激执行记录 | 治疗执行证据 | 方案是否按要求执行 |
| EEG 传统指标与 REVE 表征 | 辅助生理证据 | 是否观察到可信、可重复的脑状态变化 |
| Agent | 证据组织与解释工具 | 如何汇总结果、依据、限制和建议 |
| 医生/治疗师 | 最终责任主体 | 是否接受结论及如何用于临床观察 |

### 2.2 预期业务价值

- 将 EEG 从附件升级为可追踪的辅助证据；
- 提高质控自动化程度，减少无效数据进入报告；
- 建立患者级、多疗程的脑状态表征；
- 为后续科研合作与注册验证积累标准化数据；
- 用统一评测决定是否值得继续投入基础模型，而不是依赖论文排名；
- 为 Agent 提供结构化、可核查的分析结果，降低生成式模型幻觉风险。

## 3. 总体流程

```text
患者与疗程信息
      +
训练前/后 EEG ──→ 数据脱敏与完整性检查
      +                         ↓
训练行为结果             固定版本预处理
      +                         ↓
刺激执行记录              自动/人工质控
                                ↓
                   ┌────────────┴────────────┐
                   │                         │
             传统指标/EEGNet             REVE 表征
                   │                         │
                   └────────────┬────────────┘
                                ↓
                       按患者隔离评估
                                ↓
             质控结果 / Pre-Post / 多疗程趋势
                                ↓
                       结构化证据对象
                                ↓
                  Agent 报告草稿 → 医生复核
```

REVE 位于“确定性模型工具层”，不是 Agent 自身。Agent 只能读取已经完成版本锁定、质量判断和统计计算的结果。

## 4. 验证任务与优先级

### 4.1 P0：数据接入与表征稳定性

验证内容：

- EDF/BDF/BrainVision/NumPy 数据是否能够统一读取；
- 通道名称能否映射到标准 10-20 系统；
- 数据能否固定重采样至 200 Hz；
- REVE 是否稳定输出有限数值向量；
- 相同输入、相同版本和相同随机种子是否产生一致结果；
- 缺失通道、坏通道和短记录是否被正确拒绝或降级。

验收条件：

| 指标 | 通过标准 |
|---|---:|
| 文件解析成功率 | ≥98% |
| 必需元数据完整率 | ≥95% |
| 标准通道映射成功率 | ≥95% |
| REVE 有效输出率 | ≥98% |
| 输出 NaN/Inf 比例 | 0% |
| 同输入重复推理最大绝对差 | ≤1e-5 |

### 4.2 P0：EEG 数据质量辅助识别

第一版定义三类 Session 质量：合格、可疑、不合格。规则算法先识别掉包、平线、饱和、时长不足和通道缺失；模型重点识别眼动、肌电、运动伪迹和复杂异常。

主要指标：

- Subject-level AUROC；
- 不合格数据的 sensitivity；
- 合格数据的 specificity；
- Subject-level accuracy；
- 校准误差和不确定样本比例。

PoC 目标：

| 指标 | 目标值 |
|---|---:|
| Subject-level AUROC | ≥0.80 |
| Sensitivity | ≥0.90 |
| 对应 Specificity | ≥0.70 |
| 医生复核一致率 | ≥85% |

这些数值是内部产品决策阈值，不是医疗器械性能声明。若样本量不足，只报告置信区间和探索结果，不宣称通过临床验证。

### 4.3 P1：训练前后可比性与患者内变化

系统必须先判断 Pre/Post 是否可比较，再计算变化。可比性条件包括：采集任务、参考方式、通道集合、有效时长、预处理版本、刺激结束至后测间隔和质量等级。

输出包括：

- 可比较、有条件可比较、不可比较；
- 传统 EEG 指标差值；
- REVE 表征距离；
- 变化是否超过患者自身历史波动；
- EEG 与训练结果一致、矛盾或暂无明确关系；
- 适用限制和缺失证据。

该任务不设置“治疗有效”分类准确率。PoC 只验证同条件重复测量的稳定性、患者内变化的可重复性，以及与行为指标的探索性关联。

### 4.4 P2：多疗程趋势和辅助报告

至少三次可比较疗程后，系统才显示趋势：

- 行为指标变化；
- 每次 EEG 质量；
- 每次 Pre/Post 表征变化；
- 相似变化重复次数；
- 异常或不可比较疗程；
- 报告中的证据等级与限制。

Agent 只生成报告草稿，医生可以接受、修改、删除结论或退回重新分析；全部操作必须留痕。

## 5. 数据准备计划

### 5.1 三层数据规模

| 阶段 | 建议数据 | 目的 |
|---|---|---|
| 冒烟验证 | 10 个匿名 Session | 验证读取、预处理和 REVE 输出 |
| PoC 建模 | 至少 30 名患者，每名尽量包含成对 Pre/Post | 验证技术可行性和初步跨患者表现 |
| 前瞻验证 | 建议 100 名以上患者并另行做样本量估算 | 验证泛化、稳定性和临床流程适配 |

30 名患者只用于探索，不足以支持正式临床性能声明。后续样本量需要根据目标效应、阳性比例、置信区间宽度和主要终点重新计算。

### 5.2 最小数据字典

| 字段 | 示例 | 是否必需 |
|---|---|---|
| patient_id | P0001，脱敏编号 | 是 |
| course_id | C202609001 | 是 |
| session_id | S202609001-01 | 是 |
| phase | PRE / POST | 是 |
| eeg_file | 受控存储路径 | 是 |
| sfreq | 500 | 是 |
| channel_names | Fp1、Fp2、F3等 | 是 |
| reference | linked-mastoids | 是 |
| acquisition_protocol | resting-eyes-open-v1 | 是 |
| quality_label | PASS / REVIEW / FAIL | P0质控必需 |
| behavior_summary | 正确率、反应时间、提示次数 | 联合分析必需 |
| stimulation_summary | 实际开始、结束、中止、异常 | 联合分析必需 |
| model_version | reve-base + revision | 生成结果时写入 |
| preprocessing_version | prep-v1.0.0 | 生成结果时写入 |

### 5.3 数据划分原则

- 同一患者的任何片段不得同时进入训练集和测试集；
- 主评估使用 patient-grouped split；
- 数据较少时采用嵌套的 GroupKFold；
- 阈值只能在训练/验证数据上选择；
- 测试集只在方案冻结后运行一次；
- 报告片段级指标时，必须同时报告 Session 级和患者级指标；
- 必须检查 REVE 预训练数据与公开测试集是否重叠，避免错误解释公开成绩。

## 6. 八周里程碑与决策门

| 周期 | 工作重点 | 交付物 | 决策门 |
|---|---|---|---|
| 第1周 | 权重、许可、数据和安全审查 | 权重访问记录、数据清单、任务定义 | G0：是否具备合法数据与明确任务 |
| 第2周 | 数据合同与标准读取 | 数据清单、校验器、10例冒烟结果 | 继续/修复数据 |
| 第3周 | 固定预处理与REVE接入 | 版本化预处理、表征输出、异常报告 | G1：有效输出率是否≥98% |
| 第4周 | EEGNet与传统指标基线 | 基线评估报告 | 判断任务是否本身可学习 |
| 第5周 | REVE冻结编码器评估 | AUROC、灵敏度、特异度、患者级结果 | 判断REVE是否有增量价值 |
| 第6周 | 微调或轻量适配、稳健性检查 | 模型对比、消融、置信区间 | G2：是否值得进入影子运行 |
| 第7周 | Pre/Post与报告原型 | 变化分析、结构化证据、报告样例 | 临床解释边界评审 |
| 第8周 | 总结与管理层复盘 | PoC总报告、预算与下一阶段建议 | G3：产品化/补数/调整/停止 |

### 6.1 停止条件

出现任一情况时，不进入下一阶段：

- 权重协议与计划部署方式存在无法接受的冲突；
- 数据无法稳定映射通道或重采样；
- 数据质量标签缺少可复核标准；
- 跨患者测试接近随机且不优于 EEGNet；
- 模型增益只能通过患者泄漏或片段随机划分获得；
- 输出对通道缺失、设备批次或预处理变化极度敏感；
- 临床团队无法定义结果应如何使用和不得如何使用；
- 计算与维护成本明显高于模型带来的增量价值。

## 7. 模型策略

### 7.1 比较顺序

1. 传统可解释指标；
2. EEGNet 从头训练；
3. REVE 冻结编码器 + Logistic Regression；
4. REVE 冻结编码器 + 小型 MLP；
5. 只有第 3/4 步显示增量价值后，才进行 REVE 微调；
6. 算力或延迟不满足时，再加入 CBraMod 作为轻量备选。

### 7.2 为什么不能只跑 REVE

没有基线就无法判断效果来自基础模型、数据预处理还是任务本身。REVE 必须在相同数据划分、相同标签、相同指标和相同测试集上与 EEGNet、传统指标比较。

### 7.3 模型通过标准

质控任务满足以下任一条件才认为 REVE 具有继续价值：

- 相比 EEGNet，Subject-level AUROC 提升至少 0.03，且 bootstrap 95% CI 不显示明显退化；
- AUROC相当（差值绝对值≤0.02），但跨设备稳定性、少样本表现或校准明显更好；
- 在不降低 sensitivity 的情况下，specificity 提升至少 0.05；
- 能提供传统指标和 EEGNet 无法获得的、经过重复验证的患者内表征稳定性。

## 8. 技术和安全设计

### 8.1 版本锁定

每次结果必须记录：

- Git commit；
- REVE Hugging Face revision；
- 权重文件 SHA-256；
- Python与依赖锁文件；
- 预处理版本；
- 通道映射版本；
- 数据集快照版本；
- 训练配置和随机种子；
- 指标代码版本。

### 8.2 `trust_remote_code` 控制

REVE 官方 Hugging Face 示例使用 `trust_remote_code=True`。PoC 环境必须先下载并审查自定义代码，固定 revision 后离线加载；产品环境不得每次运行自动拉取最新代码。

### 8.3 隐私与权重协议

- 患者数据在进入研究目录前完成脱敏；
- 不向 Hugging Face 或外部服务上传 EEG；
- 模型推理在批准的本地或内网环境完成；
- 不进行患者身份识别、成员推断或模型反演；
- 不公开托管或向客户再分发 REVE 权重及其衍生权重；
- 私有化部署前必须取得法务对权重条款的书面意见；
- 日志不得包含姓名、身份证号、病历号等直接身份信息。

## 9. 人员、资源与职责

| 角色 | 投入建议 | 主要职责 |
|---|---:|---|
| 项目负责人/产品 | 0.3 FTE | 目标、范围、决策门与跨部门协调 |
| EEG算法工程师 | 1.0 FTE | 预处理、REVE、基线、评估与报告 |
| 数据工程师 | 0.5 FTE | 数据合同、脱敏、质量校验与版本管理 |
| 临床专家 | 0.2 FTE | 标签规范、盲法复核、解释边界 |
| 后端/平台工程师 | 0.2 FTE | 实验记录、结果接口和内网运行环境 |
| 测试/合规 | 0.2 FTE | 可复现性、安全、权重协议和审计 |

计算资源建议：

- 冒烟推理可使用 Apple Silicon 或 CPU；
- 批量表征和线性探测建议使用一张 12–16 GB 显存 GPU；
- 完整微调建议使用 24 GB 以上显存或采用梯度累积/参数高效微调；
- 预留加密数据盘、模型缓存和实验产物空间；
- 不进行从头预训练。

## 10. 交付物

PoC 结束时必须提供：

1. 数据字典、数据清单和患者隔离划分清单；
2. 固定版本的预处理流水线；
3. REVE离线表征适配器；
4. EEGNet和传统指标基线；
5. 患者级模型评估报告；
6. 失败样本、设备批次和坏通道敏感性分析；
7. Pre/Post及多疗程探索报告；
8. 一份医生可复核的辅助报告样例；
9. 权重、代码、数据和结果的审计记录；
10. 下一阶段建议及预算，或明确停止理由。

## 11. 风险清单

| 风险 | 影响 | 控制措施 |
|---|---|---|
| 样本量不足 | 指标波动大 | 报告置信区间；限制为探索性结论 |
| 患者数据泄漏 | 指标虚高 | patient-grouped split；独立检查划分清单 |
| 预训练数据重叠 | 公开成绩偏高 | 自有患者独立测试；记录数据来源 |
| 通道/设备差异 | 表征漂移 | 标准通道映射、设备批次分析、缺失通道压力测试 |
| 标签不一致 | 模型学习错误 | 双人复核、争议仲裁、记录一致率 |
| Pre/Post自然波动 | 误判治疗作用 | 同条件重复测量、患者自身历史基线 |
| 权重不可再分发 | 阻碍私有部署 | 先法务审查；产品化前取得许可或更换模型 |
| 生成式解释越界 | 医疗误导 | Agent仅读结构化结果；医生最终复核 |
| 算力与延迟过高 | 无法产品化 | 冻结编码、离线分析、轻量模型对照 |

## 12. 管理层最终决策模板

第 8 周只允许四种决策：

### A. 进入产品化影子运行

适用条件：REVE在患者级评估上稳定优于或补充基线，数据与许可条件可控，临床解释边界清晰。

### B. 继续收集数据

适用条件：技术链路成立，但样本不足或置信区间过宽，暂不能判断模型价值。

### C. 调整任务

适用条件：治疗效果预测不可行，但数据质控、异常识别或状态表征具有价值。

### D. 停止投入

适用条件：无跨患者增量价值、数据条件不匹配、许可不可接受或维护成本过高。

---

## 13. 技术实施文件结构

PoC 与现有产品运行代码隔离，计划新增：

```text
research/reve-poc/
├── README.md                         # 环境、数据边界与运行顺序
├── pyproject.toml                    # Python 3.11与固定依赖
├── uv.lock                           # 可复现依赖锁
├── configs/
│   ├── base.yaml                     # 通道、采样率、窗口和随机种子
│   ├── qc.yaml                       # 质量分类任务
│   └── pre_post.yaml                 # 患者内变化任务
├── src/reve_poc/
│   ├── data_contract.py              # Session清单与字段校验
│   ├── channel_map.py                # 标准电极名称映射
│   ├── preprocessing.py              # 固定滤波、重采样、切窗
│   ├── reve_encoder.py               # 固定revision的REVE适配器
│   ├── eegnet_baseline.py            # EEGNet对照模型
│   ├── split.py                      # 患者级划分
│   ├── metrics.py                    # AUROC、灵敏度、特异度等
│   ├── longitudinal.py               # Pre/Post与多疗程分析
│   └── evidence_schema.py            # Agent可读取的结构化结果
├── scripts/
│   ├── audit_manifest.py             # 数据审计
│   ├── extract_embeddings.py         # 批量生成REVE表征
│   ├── train_qc.py                    # 基线与REVE分类头训练
│   └── build_report.py               # 生成评估报告
└── tests/
    ├── test_data_contract.py
    ├── test_channel_map.py
    ├── test_preprocessing.py
    ├── test_reve_encoder.py
    ├── test_split.py
    ├── test_metrics.py
    ├── test_longitudinal.py
    └── test_evidence_schema.py
```

PoC的原始数据、模型权重、缓存、输出图表和患者级中间文件不进入 Git。

## 14. 分步实施任务

### Task 1：建立隔离环境与权重审计

**Files:**
- Create: `research/reve-poc/README.md`
- Create: `research/reve-poc/pyproject.toml`
- Create: `research/reve-poc/.gitignore`
- Create: `research/reve-poc/scripts/verify_environment.py`
- Test: `research/reve-poc/tests/test_environment.py`

- [ ] **Step 1：写环境失败测试**

测试应断言 Python ≥3.11、Torch ≥2.6、模型revision非空、权重SHA-256为64位十六进制字符，并在缺少任一条件时失败。

- [ ] **Step 2：运行测试确认失败**

Run: `python -m pytest research/reve-poc/tests/test_environment.py -v`

Expected: FAIL，原因是 `verify_environment.py` 尚不存在。

- [ ] **Step 3：实现环境检查器**

配置中固定：

```toml
[project]
name = "reve-poc"
requires-python = ">=3.11,<3.13"
dependencies = [
  "torch>=2.6.0",
  "transformers==4.56.2",
  "mne>=1.9.0",
  "numpy>=2.2.4",
  "scikit-learn>=1.6.1",
  "safetensors>=0.5.3",
  "pydantic>=2.10",
  "pyyaml>=6.0",
  "pytest>=8.3"
]
```

`.gitignore` 必须排除 `data/`、`weights/`、`outputs/`、`.env`、Hugging Face缓存和患者级导出文件。

- [ ] **Step 4：安装并验证**

Run: `cd research/reve-poc && uv sync && uv run pytest tests/test_environment.py -v`

Expected: PASS，并输出 Python、Torch、设备类型、模型revision和权重哈希。

- [ ] **Step 5：提交本地变更**

Run: `git add research/reve-poc && git commit -m "chore: scaffold isolated REVE PoC environment"`

注意：只做本地提交；未收到“同步 GitHub”指令前不得推送。

### Task 2：定义患者级数据合同

**Files:**
- Create: `research/reve-poc/src/reve_poc/data_contract.py`
- Create: `research/reve-poc/configs/base.yaml`
- Create: `research/reve-poc/tests/test_data_contract.py`

- [ ] **Step 1：写数据合同测试**

测试覆盖：缺少 `patient_id`、`session_id`、`phase`、`sfreq`、`channel_names` 或 `reference` 时拒绝；`phase`只允许`PRE`和`POST`；直接身份字段出现时拒绝。

- [ ] **Step 2：运行测试确认失败**

Run: `cd research/reve-poc && uv run pytest tests/test_data_contract.py -v`

Expected: FAIL，原因是数据模型尚未定义。

- [ ] **Step 3：实现数据合同**

核心类型固定为：

```python
class EEGSessionRecord(BaseModel):
    patient_id: str
    course_id: str
    session_id: str
    phase: Literal["PRE", "POST"]
    eeg_file: Path
    sfreq: float
    channel_names: list[str]
    reference: str
    acquisition_protocol: str
    quality_label: Literal["PASS", "REVIEW", "FAIL"] | None = None
```

校验器拒绝姓名、身份证号、手机号和真实病历号字段。

- [ ] **Step 4：运行测试确认通过**

Run: `cd research/reve-poc && uv run pytest tests/test_data_contract.py -v`

Expected: PASS。

- [ ] **Step 5：提交本地变更**

Run: `git add research/reve-poc && git commit -m "feat: define deidentified EEG session contract"`

### Task 3：实现固定版本预处理

**Files:**
- Create: `research/reve-poc/src/reve_poc/channel_map.py`
- Create: `research/reve-poc/src/reve_poc/preprocessing.py`
- Create: `research/reve-poc/tests/test_channel_map.py`
- Create: `research/reve-poc/tests/test_preprocessing.py`

- [ ] **Step 1：写通道与预处理测试**

测试断言：`FP1`映射为`Fp1`；重复通道拒绝；未知通道进入审查列表；500 Hz输入输出为200 Hz；输出形状为`[window, channel, time]`；NaN、平线和时长不足被标记。

- [ ] **Step 2：运行测试确认失败**

Run: `cd research/reve-poc && uv run pytest tests/test_channel_map.py tests/test_preprocessing.py -v`

Expected: FAIL。

- [ ] **Step 3：实现预处理流水线**

固定顺序：读取原始数据 → 单位统一为V → 通道标准化 → 基础完整性检查 → 带通/陷波参数化处理 → 重采样200 Hz → 固定窗口切分 → 记录排除原因。每个输出窗口保留`patient_id/session_id/phase/window_index`。

- [ ] **Step 4：运行测试确认通过**

Run: `cd research/reve-poc && uv run pytest tests/test_channel_map.py tests/test_preprocessing.py -v`

Expected: PASS；合成10秒、500 Hz数据重采样后长度为2000点。

- [ ] **Step 5：提交本地变更**

Run: `git add research/reve-poc && git commit -m "feat: add versioned REVE preprocessing pipeline"`

### Task 4：接入固定revision的REVE编码器

**Files:**
- Create: `research/reve-poc/src/reve_poc/reve_encoder.py`
- Create: `research/reve-poc/scripts/extract_embeddings.py`
- Create: `research/reve-poc/tests/test_reve_encoder.py`

- [ ] **Step 1：写编码器契约测试**

测试使用小型假模型断言输入为`[batch, channel, time]`、采样率为200 Hz、通道坐标维度为`[batch, channel, 3]`、输出不存在NaN/Inf，并记录模型revision与哈希。

- [ ] **Step 2：运行测试确认失败**

Run: `cd research/reve-poc && uv run pytest tests/test_reve_encoder.py -v`

Expected: FAIL。

- [ ] **Step 3：实现离线安全加载**

适配器必须显式传入`model_revision`，首次审查后保存自定义代码，正式批量运行使用本地文件和`local_files_only=True`。输出结构固定为：

```python
@dataclass(frozen=True)
class EmbeddingResult:
    session_id: str
    window_index: int
    vector: np.ndarray
    model_id: str
    model_revision: str
    weights_sha256: str
    preprocessing_version: str
```

- [ ] **Step 4：运行10例冒烟测试**

Run: `cd research/reve-poc && uv run python scripts/extract_embeddings.py --manifest data/smoke/manifest.csv --config configs/base.yaml`

Expected: 10个Session全部产生审计清单；有效输出率、失败原因和运行耗时写入`outputs/smoke/summary.json`。

- [ ] **Step 5：提交本地变更**

Run: `git add research/reve-poc && git commit -m "feat: add audited offline REVE encoder"`

### Task 5：建立患者隔离评估与EEGNet基线

**Files:**
- Create: `research/reve-poc/src/reve_poc/split.py`
- Create: `research/reve-poc/src/reve_poc/eegnet_baseline.py`
- Create: `research/reve-poc/src/reve_poc/metrics.py`
- Create: `research/reve-poc/scripts/train_qc.py`
- Create: `research/reve-poc/tests/test_split.py`
- Create: `research/reve-poc/tests/test_metrics.py`

- [ ] **Step 1：写患者泄漏和指标测试**

测试断言训练、验证和测试患者集合两两不相交；片段概率先按Session/患者聚合；固定混淆矩阵能够得到预期sensitivity和specificity；单类别测试集明确报错。

- [ ] **Step 2：运行测试确认失败**

Run: `cd research/reve-poc && uv run pytest tests/test_split.py tests/test_metrics.py -v`

Expected: FAIL。

- [ ] **Step 3：实现统一评估**

同一划分上运行：传统指标、EEGNet、REVE+Logistic Regression、REVE+MLP。输出AUROC、sensitivity、specificity、accuracy、F1、校准曲线、bootstrap 95% CI和患者级混淆矩阵。

- [ ] **Step 4：运行训练与测试**

Run: `cd research/reve-poc && uv run python scripts/train_qc.py --manifest data/poc/manifest.csv --config configs/qc.yaml`

Expected: `outputs/qc/model-comparison.json`包含四组模型、相同患者划分和完整指标。

- [ ] **Step 5：提交本地变更**

Run: `git add research/reve-poc && git commit -m "feat: benchmark REVE against patient-level EEG baselines"`

### Task 6：实现患者内Pre/Post和多疗程分析

**Files:**
- Create: `research/reve-poc/src/reve_poc/longitudinal.py`
- Create: `research/reve-poc/configs/pre_post.yaml`
- Create: `research/reve-poc/tests/test_longitudinal.py`

- [ ] **Step 1：写配对与可比性测试**

测试断言不同患者不能配对；协议或参考方式不一致时返回`NOT_COMPARABLE`；质量为`FAIL`时不计算变化；三次以上可比Session才产生趋势。

- [ ] **Step 2：运行测试确认失败**

Run: `cd research/reve-poc && uv run pytest tests/test_longitudinal.py -v`

Expected: FAIL。

- [ ] **Step 3：实现纵向分析**

输出固定为`comparability`、`traditional_delta`、`embedding_distance`、`within_subject_percentile`、`repeat_count`、`behavior_relation`和`limitations`，不得包含自动生成的`effective=true/false`字段。

- [ ] **Step 4：运行测试确认通过**

Run: `cd research/reve-poc && uv run pytest tests/test_longitudinal.py -v`

Expected: PASS；不可比较样本不产生变化结论。

- [ ] **Step 5：提交本地变更**

Run: `git add research/reve-poc && git commit -m "feat: add guarded longitudinal EEG analysis"`

### Task 7：生成Agent可消费的证据对象和管理报告

**Files:**
- Create: `research/reve-poc/src/reve_poc/evidence_schema.py`
- Create: `research/reve-poc/scripts/build_report.py`
- Create: `research/reve-poc/tests/test_evidence_schema.py`
- Create: `research/reve-poc/docs/report-template.md`

- [ ] **Step 1：写证据边界测试**

测试断言证据对象必须包含来源、模型版本、预处理版本、质量、适用限制和医生复核状态；出现“确认有效”“治愈”“由刺激导致”等越界表述时拒绝发布。

- [ ] **Step 2：运行测试确认失败**

Run: `cd research/reve-poc && uv run pytest tests/test_evidence_schema.py -v`

Expected: FAIL。

- [ ] **Step 3：实现证据模式和报告模板**

报告顺序固定为：训练主要结果 → EEG数据质量 → 前后可比性 → 传统与REVE辅助变化 → 多疗程趋势 → 限制 → 医生复核。任何`NOT_COMPARABLE`结果只能显示质量与原因。

- [ ] **Step 4：生成PoC总报告**

Run: `cd research/reve-poc && uv run python scripts/build_report.py --evaluation outputs/qc/model-comparison.json --longitudinal outputs/pre-post/results.json --out outputs/reports/reve-poc-report.html`

Expected: 报告包含模型对比、患者级指标、置信区间、失败样本、数据限制和明确决策建议。

- [ ] **Step 5：运行全量测试并提交本地变更**

Run: `cd research/reve-poc && uv run pytest -v`

Expected: 全部PASS。

Run: `git add research/reve-poc && git commit -m "feat: produce reviewable REVE PoC evidence report"`

## 15. 实施完成后的检查清单

- [ ] 所有任务都有明确目标、输入、输出和停止条件；
- [ ] REVE不直接输出治疗有效性结论；
- [ ] EEGNet和传统指标作为同数据对照；
- [ ] 所有评估均按患者隔离；
- [ ] 质控、Pre/Post和报告任务边界不同；
- [ ] 权重、代码、数据和预处理版本均可追溯；
- [ ] 敏感数据和权重未进入Git；
- [ ] 报告展示置信区间、失败样本和限制；
- [ ] 医生保留最终复核权；
- [ ] 未经用户明确要求，不推送GitHub或触发Render部署。

