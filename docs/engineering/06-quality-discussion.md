# 06 测试与质量门禁意见书

| 项 | 内容 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-10-13 |
| 状态 | **待监督人评审**（工程实施计划三讨论席之一：测试与质量门禁） |
| 输入 | `01-feature-inventory.md` v1.0（F-001~F-177）；`02-consistency-audit.md` v1.0（RL1~RL15 实现红线、A/N/G 编号）；`03-decomposition.md` v1.0（C-01~C-31、测试策略映射、R-E1~R-E9）；`../我在游戏觉醒系统-游戏设计方案.md` v3.1（§11/§17/附录 F）；`../DOCUMENT_STANDARDS.md` |
| 甲方硬性要求 | 每个开发点都有**单元/逻辑/集成/回归/链路**五级测试，业务逻辑完整真实覆盖不造假；警惕硬编码/伪逻辑/伪代码/假占用/假成功/本机路径；每任务过 code-review 严格验证并修复复验直到完全没问题 |
| 现场事实 | `D:\game` 已初始化 git（RL13 可机械验证基线可达性）；`D:\game\game` 尚未创建——**本意见书的门禁应作为 C-30/C-01 开工输入前置落地，而非事后补挂** |

---

## 0. 结论速览

- **五级测试 = 一条金字塔**：单元（xUnit 纯函数，秒级）→ 逻辑（状态机/规则引擎，秒级）→ 集成（EraCore 组合 + Godot headless，分钟级）→ 回归（金样 JSON + 1000 波模拟）→ 链路（headless E2E：军令→战报、存档往返、R2 关闭系统通关）。Layer 0~4 组件的单元+逻辑+集成前两级 **100% 跑在 `dotnet test` 不启动 Godot**（继承 03 §4，这是"真实覆盖"硬要求唯一可行的低成本路径）。
- **CI 自动检查 23 条**：15 条红线扫描（RL1~RL15 逐条设防）+ 8 条测试质量通用检查（T1~T8：残留标记/src↔test 映射/F-ID 关联/空断言/越权 mock/覆盖率阈值/架构守卫/金样变更独立性）。20 条全自动、3 条半自动（RL14/RL15/T4，机械扫不全的由审查记录字段与运行时自校验兜底）。
- **每任务门禁 = DoD 清单（§2）全项勾选 + CI 23 项全绿 + code-review 双轴 PASS + 审查记录归档**，四者缺一不可合入。
- **code-review 循环**：每任务一分支一 squash commit；Standards 轴 = DOCUMENT_STANDARDS + 新增 CODE_STANDARDS.md，Spec 轴 = 任务卡；BLOCKER/MAJOR 必须修复后**下一轮复验**，直至某轮 0 BLOCKER/0 MAJOR 才 PASS，全部轮次留档不覆盖（§4）。
- **网络测试**：offline 协议机（cassette 驱动 Fake transport）CI 必跑；online 真调用（智谱/MiMo）仅显式开启（`SKIP_ONLINE=0` 且有 Key），断言只锁结构不锁内容；**cassette 必须由录制器生成、禁止手写**（§5）。
- **性能门禁双口径**：CI headless 只测**逻辑预算**（sim tick P99 ≤12ms、流场 P99 ≤8ms），不冒充 60fps；真机（有 GPU）窗口化采样 60s，P95 ≤16.6ms 且无 >50ms 帧，报告 JSONL 入档——防"headless 数字当帧率"的假成功（§6）。
- **最大风险 = R-E1**（Godot C# headless 测试跑法）：五级中集成/链路/性能三级全部押在它上面，它失败则这三级门禁整体落空；缓解=逻辑 100% 下沉 EraCore（Godot 层测试占比目标 <20%）+ gdUnit4Net 主、自研最简 runner 兜底 + 预制作第一周先行打通冒烟。

---

## 1. 五级测试：定义与落地方法（本项目语境）

> 继承 03 §4 的组件类型×测试矩阵，将其"四层+链路"细化为甲方要求的五级。每级给出：被测对象 / 运行环境 / 驱动方式 / 断言对象 / 本项目典型用例 / 通过判定。五级是**逐级包含的包含关系**（高层可复用低层产物），但**互不替代**——每任务按 §2 的映射表逐级声明"已测/N-A（不适用）"。

### 1.1 单元测试（Unit，xUnit 纯函数级）

| 项 | 内容 |
|---|---|
| 被测对象 | EraCore 内**单一纯函数/静态规则/数据结构**：输入→输出，无 I/O、无 Godot、无墙钟（时间经 `IClock`、随机经 `IRng` 注入固定 Fake，03 §1.2 端口纪律） |
| 运行环境 | `dotnet test`（`D:\game\game\src\EraCore.Tests`，net8.0，不启动 Godot，全量秒级） |
| 驱动方式 | 直接函数调用 + `[Theory]` 表驱动（数据行含**合法/边界/非法**三类） |
| 断言对象 | 返回值/异常/状态字段的具体值（禁"不抛异常即过"，见 §2.2） |
| 典型用例 | F-021 效率 `min(供给率)`、需求≤0 恒 1；F-029 伤害 `max(d×衰减−甲, 10%×d×衰减)`（高甲保底/负甲 clamp 0/溅射边缘 40%/先衰减后减甲次序）；F-024 资源池上限 `2000×仓库等级×(1+0.2)`；F-048 胜波 t=0.3→13、t=1.2→22；F-049 乘子表 12 单元格逐点；F-051 研究成本箭楼 n=90→≈3000 |
| 通过判定 | 每个新 src 文件有对应 test 文件（T2）；关键公式模块**分支覆盖 100%**（纯公式分支有限、全表驱动，可达）；测试名含 F-ID（T3） |
| 产物 | `EraCore.Tests/Unit/**`；覆盖率报告（coverlet） |

### 1.2 逻辑测试（Logic，状态机与规则引擎级）

| 项 | 内容 |
|---|---|
| 被测对象 | **多 tick 状态转移 + 规则组合**：状态机（游戏循环 F-001、五段管线 F-065、自纠 F-068）、规则引擎（波次生成器 F-030、权限仲裁 F-063、系统点预算 F-064、卷轴仲裁 F-037、信任度 F-076、科技闸门 F-050、全托管口径 F-093） |
| 运行环境 | 同单元（`dotnet test`，秒级）——与单元级的差别不在工具而在**被测面**：从"单函数"升为"事件序列驱动的规则集+状态" |
| 驱动方式 | 场景剧本：播种 RNG + 固定 IClock + 事件/命令序列；断言**终态 + 不变式**（如：预算永不透支、危险位永不自动允许、单轮动作 ≤3、同塔同时仅 1 张最高优先级卷轴） |
| 典型用例 | ①权限仲裁全矩阵：19 动作 × {allow/ask/deny} × 危险位 × 关卡级自动允许勾选 × 随关卡存档持久；②自纠：注入必失败动作→恰好 3 次尝试（1+2 重试）后终止并汇报；③卷轴：tick 限速（1s 内第 2 次判定被拒）、优先级抢占、常驻上限溢出拒绝、系统指令压制至波末；④信任度：事件序列→分数轨迹（+2/−5 封顶/±15s 豁免/±1 回归 50/跨代重置）；⑤科技：第 2 项并行研究被拒 |
| 增强 | 性质测试（FsCheck）：波次数量恒落 [0,40×乘子]、效率恒 ∈[0,1]、减免叠乘恒 ≥50% 下限——性质断言比样例断言更难造假 |
| 通过判定 | 状态矩阵用例数 ≥ 规则表单元格数（如权限矩阵 ≥19×关键维度数）；每个公开不变式至少 1 条**故意违反应被拦截**的负向用例 |
| 产物 | `EraCore.Tests/Logic/**` |

### 1.3 集成测试（Integration，EraCore 组合 + Godot headless 场景）

| 项 | 内容 |
|---|---|
| 被测对象 | **多个 EraCore 组件的真实组合**（不 mock 被测系统，只 mock 端口）+ **Godot 侧适配层/场景** |
| 运行环境 | 两级：**(a) EraCore 组合**——仍在 `EraCore.Tests` 内，秒~分钟级；**(b) Godot headless**——gdUnit4Net（VSTest 兼容，经 `GODOT_BIN=D:\game\tools\godot\Godot.exe` 拉起 headless）；CI 为 Windows runner + 便携 Godot + `.godot` 导入缓存（R-E1 缓解） |
| 典型用例 | (a) C-06+C-07+C-11 迷宫+供弹共存（断链降速不停摆、瓶颈事件可观测）；C-09+C-10 伤害×护盾×Boss 状态乘子（×1.5/×1.3）；C-14+C-03 预算扣点经注册表无旁路（RL6 的反射守卫）；C-04 存档 SQLite/Web JSON **双后端参数化跑同一套测试**（R-E2 缓解）。 (b) 场景加载+节点树断言；端口适配器（IClock 桥/IHttpPort Thread+call_deferred）；快照→MultiMesh 脏标记同步；关闭系统降级路径（R2） |
| 架构守卫 | xUnit 反射断言 `EraCore.dll` 引用集不含 `GodotSharp`（03 §1.2-4，防边界腐蚀 R-E6）——归入本级 CI 必跑 |
| 通过判定 | Godot 层组件测试占比目标 <20%（逻辑全下沉）；集成测试目录出现对 EraCore 内部类的 mock 即红（T5） |
| 产物 | `EraCore.Tests/Integration/**` + `game/tests/gdunit/**` |

### 1.4 回归测试（Regression，金样 JSON + 大规模模拟）

| 项 | 内容 |
|---|---|
| 被测对象 | **数值与行为的可复现性**：任何代码/数据变更后，已冻结的正确输出不得漂移 |
| 运行环境 | `dotnet test` + 金样框架（C-30 交付）；短版（四组验算）PR 必跑，长版（1000 波）nightly |
| 金样清单（首批） | ①**波次表金样**：第 5/10/20/30 波验算（F-171：6 散兵 360EHP→2 箭楼 6s；T2 上场护甲吃 23%；精英线 4 攻城锤→3 弩炮；4 火油 57s<107s）；②**伤害表金样**：每塔×敌甲扫描矩阵的规范化输出；③**平衡快照**：同 seed+同命令流→`SimSnapshot` 规范化哈希（确定性回放，03 §1.2-2）逐 checkpoint 相等；④乘子表/方案卡/存档的规范化 JSON |
| 1000 波模拟 | 播种确定性跑 1000 波，输出双轨对比：逐 checkpoint 快照哈希（严格相等）+ 统计摘要（波次构成/资源曲线/敌数分布，分位数容差 ±ε）——防止"逐波脆断言"逼人改金样，也防止"只比均值"放过漂移 |
| **防"改金样凑绿"** | 金样更新必须：`--update-golden` 显式重生成 + **独立 commit**（T8 检查金样文件与产品代码不得同 commit 变更）+ commit message 写明变更依据（任务 ID/裁定编号），并过 code-review |
| 通过判定 | 全部金样 diff 为空；1000 波统计摘要落容差带内；数值漂移即红（吃掉 N1/N2 修复，G5） |
| 产物 | `golden/**`（版本库内）+ 每次运行的金样 diff 报告（CI artifact） |

### 1.5 链路测试（E2E，端到端）

| 项 | 内容 |
|---|---|
| 被测对象 | **玩家真实路径的完整链路**，跨 EraCore+Godot+持久化+外部协议边界 |
| 运行环境 | Godot headless + C-30 E2E harness（命令脚本驱动完整关卡，不依赖人工点击；GUI 黑盒测试另行用 web-gui-tester 技能抽查，不作门禁主体） |
| 首批链路 | ①**军令全链**：军令文本→L1 意图→方案卡（步骤+消耗+风险红标）→审批→执行（Schema+状态双校验）→**战报 JSONL 落盘**→回放器（F-121/F-174）加载回放（spike④ 三动作闭环的扩展）；②**存档链**：写→进程重启→读→版本 v1→v2 迁移→状态等价断言；**崩溃注入**（写到一半 kill→原子性：要么旧档要么新档，无半档）；③**R2 关闭系统链**：headless 自动通关 E1 手工关全集+全成就可得+警报中性横幅 100% 触发（G1 硬验收）；④**Web 导出冒烟**：threads-off 包无 SharedArrayBuffer 依赖、L2/L3 入口隐藏（F-144/F-164） |
| 断言对象 | 链路终点的**业务事实**（战报 JSONL 过 schema 且含五段记录；存档等价；成就齐备），不是"流程没抛异常" |
| 通过判定 | 每条链路在 CI 可重复跑（headless 无 GUI 依赖）；军令链的战报与回放器**同源可互证**（RL10） |
| 产物 | `game/tests/e2e/**` + 样例战报 JSONL 夹具 |

### 1.6 五级 × 任务类型映射（DoD 引用）

| 任务类型（01 总表"类型"列） | 单元 | 逻辑 | 集成 | 回归 | 链路 |
|---|---|---|---|---|---|
| 纯逻辑库（占 177 项主体） | 必测 | 必测 | 按组件组合表 | 改数值则更新金样 | 关键路径挂 E2E |
| 场景/Godot 适配 | N/A（禁业务逻辑） | N/A | 必测（gdUnit4Net） | 视觉规格断言（整数缩放等） | 共用 E2E |
| UI（GDScript） | N/A | N/A | 场景加载+数据桥断言 | 骨架恒定截图 diff（F-111） | GUI 黑盒抽查（非门禁主体） |
| 内容数据（JSON/美术） | schema 校验矩阵（合法/非法/恶意） | N/A | 全量内容加载冒烟 | 金样哈希+许可证/调色板审计 | N/A |
| 叙事/文案 | N/A | N/A | 模板填充冒烟（L1 逐条触发，G10） | 字数/条数核对（≤2 万字、15 条模板、4×50 条） | 演出时序断言（黄金三章 15 分钟节拍） |
| 工具/测试基建 | 元测试（守卫测试自身有效） | N/A | CI 冒烟 | 导出产物结构断言 | headless 导出冒烟 |
| 美术资产 | 规格校验器（尺寸/帧数） | N/A | — | 许可证审计+≤64 色校验 | — |

---

## 2. 每任务测试门禁（DoD 模板）与"真实覆盖"判定

### 2.1 DoD 清单（每任务合并前逐项勾选，任一未勾即不得合入）

```markdown
## DoD @ <任务卡编号> <F-ID/C-ID>
- [ ] 1 测试命名与 Trait 挂接 F-ID/C-ID（CI T3 通过）
- [ ] 2 单元测试：新增 src 文件均有对应 test 文件（T2）；纯函数表驱动含合法/边界/非法三类
- [ ] 3 逻辑测试：状态机/规则引擎覆盖全矩阵 + 至少 1 条负向（违反应被拦截）用例
- [ ] 4 集成测试：按 §1.6 映射执行；被测系统零 mock（仅端口 Fake，T5）
- [ ] 5 回归测试：涉及数值/输出格式变更时金样更新走独立 commit（T8）并写明依据
- [ ] 6 链路测试：关键路径挂 E2E 或声明复用现有链路（注明链路 ID）
- [ ] 7 覆盖率：EraCore.Sim/Agent 行 ≥85%、分支 ≥80%；关键公式模块分支 100%；全库行 ≥80%（T6）
- [ ] 8 断言质量：无恒真/空断言/仅"不抛异常"（T4 扫描 + review 抽查）
- [ ] 9 残留标记：无 TODO/FIXME/stub/占位（注释级扫描，T1）
- [ ] 10 红线扫描：RL1~RL15 相关项全绿（§3）；任务卡"红线关联"字段逐条确认
- [ ] 11 CI 全绿（lint-scan → unit/logic → integration → regression-short → perf-budget）
- [ ] 12 code-review 双轴 PASS（0 BLOCKER/0 MAJOR），审查记录已归档（§4.3）
- [ ] 13 非快乐路径证明：每个新增公开规则至少 1 条失败/边界路径测试（review 轴核）
- [ ] 14 无本机绝对路径/硬编码密钥/绕过入口（RL9/RL5/RL6 扫描）
```

### 2.2 "真实覆盖"的判定标准（反造假三支柱）

| 支柱 | 标准 | 机械检查法 |
|---|---|---|
| **分支覆盖** | 行/分支阈值见 DoD-7；**分支覆盖只是入场券不是证明**——分支高+断言弱照样是假覆盖 | coverlet 阈值门禁（T6）；分支覆盖与断言质量**双指标同时达标**才计数 |
| **断言质量** | ①每测试 ≥1 个**具体值/具体状态**断言（数值、枚举、集合内容、事件序列），"不抛异常"不计；②断言值不得从被测函数返回值**自反推导**（如 `Assert.Equal(f(x), f(x))`）；③浮点断言必须带容差并写明容差来源 | T4 启发式扫描（`Assert.True(true)`、空测试体、`Assert.Equal` 两参同表达式）+ review 抽查；金样测试天然抗自反断言 |
| **三禁** | **禁 mock 核心逻辑**：mock/Fake 白名单=七端口（IClock/IRng/IFileStore/IPersistPort/IHttpPort/ISecretStore/IJobPort）+ 跨层接口（IIntentParser/IChatProvider）；被测系统本身出现在 mock 目标即无效测试。**禁 assert true/空断言**。**禁只测 happy path**：表驱动必须含非法输入行；新增 throw/拒绝分支必须有对应"确实拒绝了"断言 | T5 正则捕获 `new Mock<…>`/`Substitute.For<…>` 泛型参数比对白名单；T4；DoD-13 由 code-review Spec 轴核 diff 内新增分支 |

### 2.3 假成功/假占用模式黑名单（本项目最可能出现的形态，review 与 CI 共同盯防）

1. **固定值冒充计算**：波次/伤害/首解率测试里直接 `return` F.5 期望值（RL1）——防：金样由数据 JSON 驱动+引擎输出，常量黑名单扫描（RL7）。
2. **mock HTTP 捏造返回当"集成测试"**（RL5）——防：§5 cassette 纪律，手写 cassette 禁止。
3. **跳过重算冒充流场**（RL2）：测试里直接注入旧路径缓存——防：流场测试禁 Skip/Mock 标记扫描+性能计数器存在性。
4. **Thread.Sleep/空循环冒充耗时**（假占用）：性能脚本里 sleep 凑时间、测试里 sleep 凑"异步"——防：perf harness 帧时间采样基于真实 tick 计时（§6），review 盯 `Thread.Sleep` 出现在非超时测试代码。
5. **只隐藏 UI 冒充"关闭系统"**（RL8）——防：R2 E2E 链路存在性+非 skip 检查。
6. **console 输出冒充战报**（RL10）——防：管线代码 `GD.Print`/`Console.WriteLine` 扫描+JSONL schema 校验。
7. **演出层另行 roll 点**（RL12）——防：事件序断言（reward 决定事件 index < 演出事件 index）。
8. **改金样凑绿**——防：T8 独立 commit+review。
9. **测试句编进文法后宣称 80% 首解率**（RL4）——防：语料与文法目录分离扫描+留出集 hash 登记。
10. **headless 帧时间冒充 60fps**——防：§6 双口径，CI 只报逻辑预算、60fps 判定仅在真机档。

---

## 3. 15 条红线的机械可检机制（CI 检查设计）

### 3.1 检查总表（23 条 = 15 红线 R 系列 + 8 通用 T 系列）

> 工具：单入口脚本 `tools/ci/redline_scan.py`（Python，零三方依赖，grep/正则+git+文件系统检查），输出逐条 PASS/FAIL/SKIP(需人工) 与 JSON 报告；任一 FAIL 退出码 1。豁免必须行内标记 `// ci-allow: R9 <任务ID 理由>` 且 review 记录引用之——**无豁免标记的命中即红**。

| # | 检查 | 红线/目标 | 判定逻辑（可执行） | 自动化 |
|---|---|---|---|---|
| R1 | 波次公式参数化 | RL1 | ①`data/waves/{era}.json` 存在且过 schema；②波次引擎源码中命中常量黑名单（由 C-29 数据表导出，见 R7）即红；③波次测试引用数据文件而非内联验算值 | 全自动 |
| R2 | 流场真实实现 | RL2 | ①grep 测试目录 `FlowField` 邻近的 `Skip`/`Mock`/`Stub` 标记；②性能计数器 API（8ms 采样接口）存在；③流场集成测试存在且非 skip | 全自动 |
| R3 | 敌路径无硬编码 | RL3 | ①grep 源码 waypoint/路径序列常量表模式（连续 ≥4 个 CellCoord 字面量）；②拆墙模式集成测试存在（文件名/测试名含 Maze+WallBreak） | 全自动 |
| R4 | 语料盲测分离 | RL4 | ①语料目录 `corpus/` 与文法/词典目录 `grammar/` 无反向引用（文法构建脚本 import/读路径不得含 corpus）；②留出集文件 hash 与登记文件一致 | 全自动 |
| R5 | BYOK 无硬编码/显式标注 | RL5 | ①grep `sk-`、`api_key\s*=\s*"[^"]+"` 字面量（模板空串豁免）；②base_url 字面量仅允许出现在 config 模板/金样目录；③transport 测试必须带 `[FakeTransport]`/`[RealTransport]` Trait，缺失即红；④运行时日志夹具扫描无 Key 模式 | 全自动 |
| R6 | 预算唯一入口 | RL6 | ①grep `bypass`/`BypassBudget`/`--test-mode` 直调后门；②反射守卫测试存在：19 动作执行器仅可经 ActionRegistry 构造 | 全自动 |
| R7 | 数值全数据驱动 | RL7 | 常量黑名单自动生成：提取 `data/**/*.json` 全部数值常量 → `tools/ci/const_blacklist.txt`；grep `src/EraCore/**`（排除 Tests/golden）命中即红（白名单：0/1/2 等结构小整数、公式守护常量清单） | 全自动 |
| R8 | 关闭系统功能级验证 | RL8 | R2 E2E 用例存在性：测试名匹配 `SystemOff.*FullClear` 且无 Skip 标记；断言源码含成就清单比对调用 | 全自动 |
| R9 | 无本机绝对路径 | RL9 | 正则 `[A-Za-z]:[\\/]` 扫描 `*.cs/*.gd/*.json`（代码与配置）；`res://`、`user://` 豁免；白名单：`tools/ci/**`、`*.md`、`.github/**` | 全自动 |
| R10 | JSONL 全管线落盘 | RL10 | ①五段管线各段 writer 代码存在（意图/计划/审批/执行/汇报 五类事件编码器）；②`src/EraCore/Agent/**` 内 grep `GD.Print\|Console.WriteLine` 即红（日志须经事件总线端口）；③战报 schema 校验测试存在 | 全自动 |
| R11 | 信任度数值系统 | RL11 | 信任度测试文件存在且用例数 ≥ 规则表行数（+2/−5 封顶/±15s 豁免/±1 回归/跨代重置/40 阈值减半/70 档位）；三档徽记由数值驱动（徽记渲染读同一分数源，代码引用检查） | 全自动 |
| R12 | 转盘先定后演 | RL12 | 事件序测试存在：断言战报 JSONL 中 reward 决定事件的序号 < 演出开始事件序号；演出代码中不得调用 RNG roll 接口（grep 演出模块 `IRng` 引用） | 全自动 |
| R13 | git 基线可达 | RL13 | `git cat-file -e 8402414^{commit}` 退出码 0；不可达则本检查 FAIL 且按 02 裁定：实现"延续 v2.1"机制前必须先恢复或重写规格（当前 D:\game 已有 .git，仍须验证该 commit 在库内） | 全自动 |
| R14 | 性能场景真实复合 | RL14 | 两层：①场景静态检查（半自动）——perf 场景文件含迷宫墙体模式+传送带段数+供弹链配置；②运行时自校验（全自动）——perf 测试自身 assert 敌数=300、弹峰值 ≥1000、迷宫覆盖率与传送带在运，不满足即测试失败（防空场景刷数字） | 半自动 |
| R15 | 附录 F 摘要不得臆造 | RL15 | 任务卡含"继承 v2.1 机制"字段时，审查记录 JSON 必须有 `rl15_checked: true` 且引用 v2.1 原文位置；CI 校验记录文件字段（**内容对照本身是人工项**，机械只强制字段与引用存在） | 半自动（字段自动/内容人工） |
| T1 | 残留标记扫描 | 反伪代码 | 仅扫**注释行**（`//`、`#`、`<!--`）中 TODO/FIXME/HACK/XXX/WIP/占位/暂不实现/待补/伪实现/stub——注：Godot `LineEdit.placeholder_text` 为合法 API，故只扫注释；交付前全仓"待补"≤5 处（DOCUMENT_STANDARDS 软标准硬化） | 全自动 |
| T2 | src↔test 映射 | 甲方要求 | 每个 `src/EraCore/**/**.cs`（排除 AssemblyInfo/纯数据类白名单）在 Tests 内存在同名/关联名测试文件；缺失列出清单 | 全自动 |
| T3 | 测试-F-ID 关联 | 甲方要求 | 每个测试类/`[Trait("Feature","F-xxx")]` 至少一个 F-ID 或 C-ID；实现 commit message 含任务卡 ID（git log 扫描） | 全自动 |
| T4 | 空/恒真断言 | 反假覆盖 | ①`Assert.True(true)`/`Assert.True(1==1)` 模式；②测试方法体内无任何 `Assert.` 调用；③`catch {}` 吞异常——启发式扫描，命中即要求改写或标注豁免（正则不可靠处由 review 兜底） | 半自动 |
| T5 | 越权 mock | 反 mock 核心逻辑 | 捕获 `new Mock<`/`Substitute.For<` 泛型参数，比对端口白名单（§2.2）；白名单外即红 | 全自动 |
| T6 | 覆盖率阈值 | 真实覆盖 | coverlet `--threshold` 门禁：全库行 80%、EraCore.Sim/Agent 行 85%/分支 80%（阈值随里程碑只升不降，变更须走契约评审） | 全自动 |
| T7 | 架构守卫 | R-E6 | 反射断言 EraCore.dll 引用集不含 GodotSharp；GDScript 仅在 `game/ui/**`（路径扫描） | 全自动 |
| T8 | 金样变更独立性 | 反改金样凑绿 | git 检查：`golden/**` 变更 commit 不得同时含 `src/**` 变更；金样 commit message 须含任务 ID+依据编号（A#/N#/裁定） | 全自动 |

### 3.2 脚本骨架（`tools/ci/redline_scan.py` 设计要点）

```python
#!/usr/bin/env python3
# 单文件、零三方依赖；输入=仓库根，输出=stdout 人读报告 + .ci/redline.json 机器报告
# 退出码：0=全 PASS；1=存在 FAIL；2=存在 NEED_MANUAL（交由 review 兜底，单独计数不阻塞但须登记）
CHECKS = [R1..R15, T1..T8]          # 每条=独立函数：scope(文件集) -> [Finding]
WHITELIST = load("tools/ci/allowlist.txt")   # 行级豁免：路径或 // ci-allow: R9 <任务ID>
CONST_BLACKLIST = generate_from("data/**/*.json")  # R7：常量黑名单自数据表导出（C-29 交付后启用）
def main():
    findings = []
    for check in CHECKS:
        findings += check(repo_root)          # 每条 Finding: (id, file, line, severity, message)
    write_report(findings)                    # 分 PASS/FAIL/NEED_MANUAL 三档
    sys.exit(1 if any(FAIL) else (2 if any(NEED_MANUAL) else 0))
```

### 3.3 CI 流水线布局（GitHub Actions，Windows runner）

| Stage | 内容 | 触发 | 时长目标 |
|---|---|---|---|
| 1 lint-scan | redline_scan.py（23 条）+ 架构守卫 | 每 PR 必跑 | <1min |
| 2 unit-logic | `dotnet test --filter Tier∈{Unit,Logic}` + 覆盖率 | 每 PR 必跑 | 分钟级 |
| 3 integration | 全量 dotnet test + gdUnit4Net（GODOT_BIN 拉起 headless，缓存 `.godot`） | 每 PR 必跑 | 分钟~十分钟 |
| 4 regression-short | 四组验算+全部金样 diff | 每 PR 必跑 | 分钟级 |
| 5 regression-long | 1000 波模拟+快照哈希 | nightly + 发版 tag | ≤30min |
| 6 perf-budget | headless 逻辑预算采样（§6 Tier A） | 每 PR 必跑 | ≤5min |
| 7 online-llm | 真调用智谱/MiMo（§5；默认 skip） | 手动/每周监督人本机 | 分钟级 |

环境同构（03 §1.2-5）：`DOTNET_ROOT=D:\dotnet`、`NUGET_PACKAGES=D:\nuget`、`GODOT_BIN=D:\game\tools\godot\Godot.exe` 写入 C-30 开发文档并在 CI 复刻（runner 用同版本便携 Godot）。

---

## 4. code-review 门禁流程

### 4.1 git 纪律

- **每任务一分支**：`task/<C-ID>-<F-ID>-<slug>`（如 `task/C09-F029-damage-engine`），自 `main` 拉出。
- **每任务一 squash commit** 合入：分支内可多 commit 探索，合入前 squash，保证"一任务=一可审变更"；commit message 首行含任务卡 ID 与 F-ID/C-ID（供 T3 扫描与追溯矩阵回填）。
- 契约（03 §6 K-1~K-12）变更=独立任务独立分支，版本号递增并通知全部消费泳道。

### 4.2 双轴定义

| 轴 | 依据文件 | 审什么 |
|---|---|---|
| **Standards 轴** | `docs/DOCUMENT_STANDARDS.md` + **新增 `docs/engineering/CODE_STANDARDS.md`**（本意见书定义其骨架，作为 C-30 首任务交付）：①分层与端口纪律（EraCore 零 Godot 引用、IClock/IRng 禁全局、GDScript 仅 UI）；②命名与注册表模式（ContentType/aliases、动作注册表唯一入口）；③测试规范（§1 五级、§2 DoD、命名 `F-ID_模块_行为`）；④禁例清单（§2.3 假成功黑名单+RL1~RL15 工程序述）；⑤资源寻址（res://、user://，RL9）；⑥密钥纪律（代码版，等同 DOCUMENT_STANDARDS #5） | 代码是否守架构与规范；边界腐蚀（R-E6）；测试是否"真覆盖"（DoD 7/8/13 为否决项） |
| **Spec 轴** | **任务卡=spec**（§4.4 模板）：验收标准原文取自 01 特性清单"验收标准"列（可测表述），叠加 02 裁定（A#）与缺口处置（P#/G#） | 实现是否逐条满足验收标准；是否越界实现/漏项；测试是否逐条对应验收标准（而非自造宽松标准） |

### 4.3 复验循环与审查记录

流程：**R1 审查 → 发现项分级 [BLOCKER / MAJOR / MINOR] → BLOCKER+MAJOR 必须修复 → 修复 commit 后 R2 复验（只验证上轮发现项+受影响面）→ 直至某轮 0 BLOCKER 且 0 MAJOR → PASS 归档**。MINOR 可延后但登记 `docs/engineering/debt.md`（编号+owner+期限；每组件挂账 ≤5 条，超限升级为 BLOCKER，防无限堆积）。

审查记录文件：`docs/engineering/reviews/REV-<YYYYMMDD>-<任务卡ID>.md`（全部轮次追加，不覆盖）：

```markdown
# REV-20261014-TK012-F029（伤害计算引擎）
| 轮次 | 审查者 | 基线 commit | 结论 | BLOCKER | MAJOR | MINOR |
|---|---|---|---|---|---|---|
| R1 | code-review 代理 | a1b2c3 | FAIL | 2 | 1 | 3 |
| R2 | code-review 代理 | d4e5f6 | PASS | 0 | 0 | 1 |

## 发现项明细
### R1-1 [BLOCKER][Spec] 10% 保底未覆盖溅射路径
- 位置：game/src/EraCore/Sim/Combat/Damage.cs:42
- 违反：任务卡验收 3（高甲触发 10% 保底）× 方案 §6
- 处置：修复于 d4e5f6；复验：R2 已确认（新增用例 F-029_Damage_高甲溅射保底）
### R1-4 [MINOR][Standards] 命名 decayFactor→falloff
- 处置：登记 debt.md#D-07
## 红线确认：RL1✅ RL7✅ RL15：N/A（非 v2.1 继承机制）
## 最终判定：PASS，合入 main @ d4e5f6；追溯矩阵 F-029 → d4e5f6
```

**合并门禁 = CI 23 项全绿 + code-review 双轴 PASS + 审查记录归档**，三者缺一不可；审查记录同时是 RL15 字段（R15 检查）的数据源。

### 4.4 任务卡模板（Spec 轴载体，C-30 交付任务卡生成模板）

```markdown
# TK-012 伤害计算引擎
- 特性：F-029｜组件：C-09｜泳道：A2｜预计层级：L3
- 来源：方案 §6 / 附录 F.5；关联裁定：A10（d=单发未衰减伤害）、A14（AOE 折减对象）
- 验收标准（引自 01 清单，逐条可测）：
  1. 高甲触发 10% 保底 2. 溅射边缘 ≥40% 3. 负甲 clamp 至 0 4. 先衰减后减甲次序
- 边界与禁例：数值只经注册表（RL7）；无 Godot 引用（T7）
- 测试要求：单元[✔ 表驱动] 逻辑[✔ ×护盾×Boss 矩阵] 集成[✔ C-09+C-10] 回归[✔ damage_table 金样] 链路[N/A]
- 红线关联：RL1 RL7；v2.1 继承：否（RL15 N/A）
- DoD：§2.1 清单 14 项
```

---

## 5. MiMo/网络测试策略（LlmGateway C-16）

### 5.1 offline / online 分级

| 档 | 内容 | 触发 | CI 角色 |
|---|---|---|---|
| **offline（必跑）** | 协议机全部分支：openai/anthropic 双协议路径与认证头、SSE 半行/跨块/`data:`/`[DONE]` 拼接、reasoning_content/content 分流、双超时（首字节+chunk 空闲）、重试矩阵（仅 429/1302/1305/5xx/网络错、指数退避 ≤3、401/400 不重试）、错误码→文案映射、上下文裁剪（工具调用对成对保留） | 每次提交 | **CI 必跑**：由 **cassette**（真实录制的 SSE 字节流，剥离 Key）驱动 `IHttpPort` Fake，零网络、零抖动 |
| **online（可选）** | 真实调用智谱 GLM / 小米 MiMo（`mimo-v2.6-pro`）端点：连接测试探针（1 消息+1 tools 透传）、function calling 返回合法动作 JSON | `SKIP_ONLINE != 1` **且** Key 存在（`config/secrets.local.json` 或环境变量 `ERA_TEST_LLM_KEY`） | CI 默认 skip（`SKIP_ONLINE=1` 或无 Key 时显式 Skip 文案"online 档未启用"，**可见而非消失**）；监督人本机每周+发版前手动跑，结果入档 |

### 5.2 假成功防范（cassette 纪律）

1. **cassette 必须由录制器生成**：文件首行 JSON 元数据 `{"generator":"llm-cassette-recorder","recorded_at":…,"endpoint_domain":…,"model":…}`；CI 校验首行 generator 字段——**手写 cassette 一律红**（RL5 的"禁 mock 固定 JSON 冒充透传验证"的机械落地）。
2. **transport 标注强制**：每条 LlmGateway 测试必须带 `[FakeTransport]`（cassette 驱动）或 `[RealTransport]`（真调用）Trait（R5-③ 扫描）；**`[FakeTransport]` 测试不得宣称覆盖"端点真行为"**——命名与断言范围受限。
3. **断言口径：只锁结构不锁内容**——online 断言=响应过 C-03 动作 Schema 校验+协议机行为正确；禁止对 LLM 文字输出做字面比对（输出不定，字面比对要么假绿要么永红）。
4. Key 纪律：Key 只经 `ISecretStore`/环境变量；测试日志与 cassette 审计不含 Key 明文（R5-④ 复扫 cassette 文件本身）。

### 5.3 分层归属

- 协议机分支 → **单元/逻辑级**（Fake transport，offline）；网关与管线组合（意图→Schema 校验→执行）→ **集成级**；BYOK 配置读写往返/版本迁移 → **回归金样**（llm.config.json v1 规范化输出）；军令经 L3 全链 → **链路级**（online 档或 cassette 驱动的 offline 等价链）。

---

## 6. 性能门禁（300 敌+1000 弹@60fps，F-166/G4/RL14）

### 6.1 双口径（防 headless 假帧率）

| 档 | 环境 | 断言 | 用途 |
|---|---|---|---|
| **Tier A：逻辑预算（CI 必跑）** | Godot headless（无渲染），固定步长驱动 | ①sim tick 处理时间 P99 ≤**12ms**（30Hz tick，为渲染/驱动留 ≥4.6ms）；②流场重算单独采样 P99 ≤**8ms**（C21/G4 口径）；③分桶数 ≤6 实测断言 | 每次 PR 守住 CPU 侧预算；**明确声明：headless 数字=逻辑预算，不代表 60fps** |
| **Tier B：真机帧率（里程碑/发版）** | 有 GPU 环境窗口化运行 60s | P95 ≤**16.6ms** 且无单帧 >50ms；300 敌+1000 弹实测在场景 | F-166 的"60fps@60s 记录入档"唯一下判处 |

### 6.2 场景与采样实现

- **场景**（RL14 真实复合）：`perf/perf_300_1000.tscn` = 极限迷宫墙体 + 供弹链（采集→加工→传送带→塔）+ 300 敌沿流场 + 弹丸峰值 ≥1000（MultiMesh）。**运行时自校验**：测试启动时 assert 敌数=300、弹峰值≥1000、迷宫覆盖率与传送带在运段数达标——场景退化（素材缺失、生成失败）即测试失败，杜绝空场景刷数字。
- **采样**：C# Stopwatch 挂帧回调，逐帧写 JSONL `{frame, delta_ms, sim_ms, flowfield_ms}`；分析脚本输出 P50/P95/P99/max 与阈值比对；报告入 CI artifact / `docs/engineering/perf-runs/`（发版留档）。
- **预算回归**：Tier A 采样同时写入金样式趋势文件，**劣化 >20% 即红**（不只是绝对阈值），防止温水煮青蛙。

---

## 7. 风险与意见

| # | 风险 | 等级 | 处置 |
|---|---|---|---|
| Q-R1 | **R-E1：Godot C# headless 测试跑法**（gdUnit4Net×Godot×.NET 版本匹配、runner 导入缓存、SDK 漂移）——集成/链路/性能三级门禁全部押注其上 | **高** | 预制作第一周先行打通最小冒烟（C-30 交付物前置）；逻辑 100% 下沉 EraCore 使 Godot 层测试占比 <20%；自研最简 runner（headless SceneTree 脚本+退出码）作兜底；CI 固定 Godot 版本+缓存 |
| Q-R2 | 覆盖率博弈：高覆盖+弱断言的"合规假覆盖" | 中 | §2.2 双指标（覆盖+断言质量）；T4/T5 机械扫描；DoD-13 非快乐路径由 review 否决 |
| Q-R3 | 金样漂移被"顺手更新"掩盖真实回归 | 中 | T8 独立 commit+依据编号；金样更新过独立 code-review |
| Q-R4 | RL13 基线不可达导致 RL15 类任务（遗迹映射等）无 spec 可依 | 中 | 开工前先跑 R13 检查；不可达即触发 02 裁定：重写规格过 review，禁止臆造 |
| Q-R5 | online 档长期无人跑 → 真端点行为漂移无人知（如中转站丢 tools） | 中 | 监督人每周+发版前手动跑入档；cassette 定期（每里程碑）重录刷新 |
| Q-R6 | 性能 Tier B 无固定 GPU 机器，真机 60fps 判定拖延 | 低 | 指定监督人本机为基准机；报告随里程碑入档 |

**给实施计划的两条意见**：① 本意见书 §2 DoD 与 §3 扫描脚本应并入 C-30 首交付（测试基础设施先于业务代码，"门禁先于开工"）；② CODE_STANDARDS.md（§4.2 骨架）建议随 Day-0 契约包一并冻结，与 K-1~K-12 同纪律管理。

## 变更记录

- v1.0（2026-10-13）：依 01/02/03 与方案 v3.1 首次成文：五级测试定义与落地、DoD 模板与真实覆盖三支柱、23 条 CI 机械检查（RL1~RL15+T1~T8）及脚本设计、code-review 双轴与复验记录格式、MiMo offline/online 分级与 cassette 纪律、性能双口径门禁。
