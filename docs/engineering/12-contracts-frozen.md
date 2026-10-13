# 12 契约冻结记录：K-1~K-8 C# 契约包（T-019~T-025）

| 项 | 内容 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-10-13 |
| 状态 | K-1/K-2/K-3/K-7/K-8 **冻结**；K-4/K-6 **草案**（07 D-21 两档范围） |
| 任务 | T-019~T-025（Wave 0；泳道 F/B/E/C/A） |
| 依据 | `03-decomposition.md` §6 契约清单；`07-decided-plan.md` D-11/D-12/D-14/D-20/D-21/D-28/D-32 与修正表 #10/#19/#20；`08-task-plan.md` W0 任务卡；`10-recovered-v21-architecture.md` §一（v2.1 §16）与 §三（19 动作字段表）；`docs/CODE_STANDARDS.md` CS-1.4/CS-2.5/CS-3.7 |
| 验证 | `dotnet build game/EraTD.sln` 零警告零错误；`python tools/ci/redline_scan.py` 0 FAIL；既有 37 测试全绿（本包不新增行为，无新测试） |

---

## 一、形态与位置

- **C# 契约本体**：`game/src/EraCore/Contracts/`（命名空间 `EraCore.Contracts.*`，七个子目录按 K 契约分域）。全部为纯 interface / enum / record，**零方法实现**——实现属后续任务（C-01~C-04、C-12~C-16）。
- **文件头声明**：每文件首行 `// T2: pure contract — K-x …, T-0xx, frozen/draft …`，注明 K 编号、任务号与冻结状态（CODE_STANDARDS CS-3.7 的文件级 T2 声明）。
- **T2 映射豁免**：整目录统一登记于 `tools/ci/allowlist.txt`（`T2: game/src/EraCore/Contracts`，目录级条目，扫描器按路径前缀匹配）。
- **非 C# 投影**：`game/contracts/`（D-02 布局保留）预留给 JSON Schema 文件等非 C# 契约投影，随 C-02（T-020 schema 矩阵）与 C-03（T-021）实现任务落地。
- **IFileStore**：已在 `game/src/EraCore/IO/IFileStore.cs` 冻结（T-028 交付，D-03 根注入语义 + DevFileStore 开发实现），按**引用声明**纳入 K-1（03 §6 认可"纳入目录或引用声明"二选一；不搬迁以保持既有测试与豁免登记的最小变更）。

## 二、逐契约登记

### K-1 领域原语与端口（**冻结**，T-019，泳道 F）

| 成员 | C# 位置 | 要点 |
|---|---|---|
| `IRng` | `game/src/EraCore/Contracts/Foundation/IRng.cs` | 种子化随机端口：`Next(min,max)`（前闭后开）/`NextDouble`/`Reseed(seed)`——确定性模拟之根（同 seed+同调用序⇒同数列；CS-1.4 禁共享 Random） |
| `IClock` | `Contracts/Foundation/IClock.cs` | `Tick`（30 Hz 固定时域，确定性时间源）/ `NowMs`（墙钟毫秒，仅诊断，禁入玩法逻辑） |
| `IGameEvent` + `IEventBus` | `Contracts/Foundation/IGameEvent.cs`、`IEventBus.cs` | 同步单线程分发（07 D-12）：`Subscribe`（返回 IDisposable 退订）/`Publish`（tick 内按触发序内联派发）；事件强制携带 `(tick, seq)`；音频/UI 经 Godot 适配器 `call_deferred` 接力 |
| `ILogSink`（含 `LogLevel`） | `Contracts/Foundation/ILogSink.cs` | 诊断日志端口（level/tick/message）；与 K-8 战报分流（RL10：日志永不是战报替身） |
| `IFileStore`（引用声明） | `game/src/EraCore/IO/IFileStore.cs` | D-03 根注入读写（Exists/ReadAllBytes/WriteAllBytes 原子建目录） |

冻结级别：**冻结**。组件：C-01（Godot 适配属 C-21）。特性：F-154（CS-1.4 时间/随机端口化亦源于此）。

### K-2 内容注册表契约（**冻结**，T-020，泳道 E）

| 成员 | C# 位置 | 要点 |
|---|---|---|
| `ContentType`（15 类） | `game/src/EraCore/Contracts/Content/ContentType.cs` | 资源/建筑/弹型/状态/敌人/波次组/时代/科技/奇观/英雄/卷轴/掉落表/关卡/本地化/**军师动作参数空间**（动词清单不可模组扩展，v2.1 §16 条 4） |
| `IContentItem` | `Contracts/Content/IContentItem.cs` | 基础接口：`Id`（`modid:contentid` 命名空间）/`ContentType`/`Aliases`（军师别名词典同源扩展） |
| `IContentRegistry` | `Contracts/Content/IContentRegistry.cs` | `Register`（按 id 排序注册、同域重名抛 `InvalidOperationException`）/`Get`/`GetByName`（全限定 id → 本体域裸 id → aliases 三级解析）/`FindByName`/`List`（id 序） |

冻结级别：**冻结**。组件：C-02（生产者）。特性：F-156（注册表模式）、F-157（ContentType+aliases）、F-023（配方数据载体）。

### K-3 系统动作 Schema 契约（**冻结**，T-021，泳道 B）

| 成员 | C# 位置 | 要点 |
|---|---|---|
| `SystemActionKind`（19 动作） | `game/src/EraCore/Contracts/Actions/SystemActionKind.cs` | 五组封闭动词表（侦察 3/建造 4/物流 2/战斗 4/管理 6）；XML 注释逐动作写字段级约束摘要（依据 `10-recovered-v21-architecture.md` §三 + v3.2 覆写：权限档 #20/A12、解锁时代 #10/N9、危险位、钳制区间、计价组） |
| `ISystemAction` | `Contracts/Actions/ISystemAction.cs` | 空壳标记接口（动作以经校验 JSON 流动，载荷类型属 C-03/C-13 实现层） |
| `ActionResult`（含 `ActionResultStatus` 7 态） | `Contracts/Actions/ActionResult.cs` | `Ok`/`Err`/`ErrCapabilityLocked`/`ErrSchemaDenied`/`ErrPermissionDenied`/`ErrBudgetDenied`/`ErrTargetInvalid` + 错误消息 + 实扣点数（失败不扣点） |
| `SchemaValidationResult`（含 `SchemaValidationError`） | `Contracts/Actions/SchemaValidationResult.cs` | 指针（schema 属性路径）+ 行号（解析期 1 基，0=未知）+ 消息——行级报错（07 D-14） |
| `ISchemaValidator` | `Contracts/Actions/ISchemaValidator.cs` | `Validate(actionJson)`：纯函数、封闭 schema、单源双投影（内部校验 schema ⇄ L3 tools JSON） |

冻结级别：**冻结**（JSON Schema 文件本体随 T-021 后续 C-03 实现任务交付，枚举与约束摘要为冻结依据）。组件：C-03（消费者 C-13/C-14/C-16/C-23）。特性：F-062、F-063（字段级约束支撑 F-070~F-074，参数空间自描述支撑 F-095）。

### K-4 意图层契约（**草案**，T-022，泳道 B）

| 成员 | C# 位置 | 要点 |
|---|---|---|
| `IntentKind`（12 意图） | `game/src/EraCore/Contracts/Intents/IntentKind.cs` | 守住X/建造/升级/出售/集火/改集火模式/物流改道/储备查询/敌情查询/设闹/巡逻/卷轴触发+停止（07 修正表 #19 冻结版，非 v2.1 原文——P-3 新设计标注） |
| `Intent` | `Contracts/Intents/Intent.cs` | `Kind`+`RawText`+`Slots`（槽位结构，值为表层字符串；多模态指认与别名解析属 C-15） |
| `IntentParseResult`（含 `IntentParseFailure`） | `Contracts/Intents/IntentParseResult.cs` | 二选一非空；失败带 `ClarifyingKey`（澄清话术本地化键→澄清卡+轮盘） |
| `IIntentParser` | `Contracts/Intents/IIntentParser.cs` | `Parse(text)`：L1/L3/L2 统一入口，纯函数，失败不抛异常 |

冻结级别：**草案**（枚举值已按 #19 冻结；槽位键集与签名随 C-15/T-052 滚动收口后升冻结）。组件：C-15（消费 C-13、C-16、C-27）。特性：F-077。

### K-6 智能体管线端口（**草案**，T-023）

| 成员 | C# 位置 | 要点 |
|---|---|---|
| `IIntentRouter`（含 `IntentRoute`/`RouteKind`） | `game/src/EraCore/Contracts/Agent/IntentRoute.cs`、`IIntentRouter.cs` | 意图路由：直答（只读意图）/计划审批/澄清/拒绝，纯函数 |
| `IPlanner`（含 `Plan`/`PlanStep`） | `Contracts/Agent/Plan.cs`、`IPlanner.cs` | 意图→Plan：步骤（动作+参数 JSON+点成本）/消耗合计（40% 单波护栏输入，N4）/风险红标（危险位）；整案原子预检（预算或资源不足整案拒绝） |
| `IApprovalGate`（含 `ApprovalDecision`） | `Contracts/Agent/ApprovalDecision.cs`、`IApprovalGate.cs` | allow/ask/deny 裁决；危险位永不自动允许；关卡级白名单不含危险位；信任档只改措辞不改能力（F.4） |
| `IActionExecutor` | `Contracts/Agent/IActionExecutor.cs` | 执行：Schema+状态双校验+注册表+预算账本唯一合法路径（RL6 无旁路）；失败回 `ActionResult` 喂 ≤2 次自纠 |
| `IAgentReporter`（含 `AgentReport`/`Finding`/`FindingVerdict`） | `Contracts/Agent/AgentReport.cs`、`IAgentReporter.cs` | 汇报：summary ≤200 字、findings ≤8、CONFIRMED/RUMOR（界线由 Schema 层强制） |
| `ILlmTransport`（含 `LlmTransportRequest`/`LlmChatMessage`/`LlmTransportResult`） | `Contracts/Agent/ILlmTransport.cs`、`LlmTransportRequest.cs`、`LlmTransportResult.cs` | LLM 翻译官端口：`SendAsync(消息+工具 schema)→候选动作 JSON`。**传输中立**：零 HTTP/SSE/供应商/端点细节（网络属 C-16 协议机与测试程序集，D-01/D-20）；候选输出在过 Schema+审批+预算前一律视为不可信（注入防护）；Key 永不过境（RL5/K-10） |

冻结级别：**草案**（签名草案，终版由 C-12 落地追认——07 D-21）。范围注记：03 §6 的 K-6 还含 SimSnapshot/SimCommand/IUnit/IProjectile/IBuilding 仿真侧签名，该部分由 C-12 泳道滚动落地（T-067），不在本包。组件：C-13（+C-16 ILlmTransport）。特性：F-065（五段管线编排）、F-079（L3 BYOK 云档）。

### K-7 存档契约（**冻结**，T-024，泳道 C）

| 成员 | C# 位置 | 要点 |
|---|---|---|
| `ISavePort` | `game/src/EraCore/Contracts/Persistence/ISavePort.cs` | `WriteCampaign`/`ReadCampaign`/`WriteLevel`/`ReadLevel`，全部带版本号参数；读侧链式前向迁移；原子写（temp+rename，崩溃注入验收 T-041） |
| `SaveVersion` | `Contracts/Persistence/SaveVersion.cs` | 常量：`Current=1`、`OldestReadable=1`（升级=契约变更流程） |
| `ICampaignMeta` | `Contracts/Persistence/ICampaignMeta.cs` | 世界种子/时代索引（E1=1…终章=6）/资源池（id→数量）/科技/遗迹注册表/各关布局快照列表 |
| `ILevelLayoutSnapshot` | `Contracts/Persistence/ILevelLayoutSnapshot.cs` | 每关布局事实（核心区格子集合**显式快照持久化，禁载档后几何重算**——07 A5 约束） |
| `ILevelSaveBlob` | `Contracts/Persistence/ILevelSaveBlob.cs` | 波界全量快照+命令尾（与回放 harness 同一代码路径，D-11）；检查点波号=⌊胜波×0.75⌋ 且 ≥10（N7） |

冻结级别：**冻结**（v1 布局；SQLite 表结构随 T-041 实现交付，Web JSON 等价映射随 T-100）。组件：C-04（消费 C-19/C-20）。特性：F-160（两级存档架构）。

### K-8 战报契约（**冻结**，T-025，泳道 F）

| 成员 | C# 位置 | 要点 |
|---|---|---|
| `IJournalWriter` | `game/src/EraCore/Contracts/Journal/IJournalWriter.cs` | JSONL 追加：一行一记录，同步有序，flush 后返回；console 输出永不是战报替身（RL10） |
| `JournalEvent`（含 `JournalEventKind`） | `Contracts/Journal/JournalEvent.cs` | 记录结构：`Kind`（Input/Plan/Validation/Report）+`(tick, seq)`（与事件总线同序域，D-12）+`PayloadJson`（STJ source generator，D-05）；轮转保留最近 50 局/200 MB 可配置（D-11）；载荷永不含 Key（RL5） |

冻结级别：**冻结**（事件目录全集枚举属 C-12 滚动物，本包冻结记录结构与写入语义）。组件：C-01（接线）/C-13（生产者）。特性：F-069（JSONL 战报；回放工具 F-121/F-174 消费）。

## 三、契约 → 组件 → 特性映射表

| 契约 | 冻结级别 | 生产组件 | 主要消费组件 | 特性（F-ID） | 关联红线 |
|---|---|---|---|---|---|
| K-1 | 冻结 | C-01 | 全部 | F-154 | CS-1.4（RL9 根注入 D-03） |
| K-2 | 冻结 | C-02 | A/B/C/D/E 全泳道 | F-156、F-157、F-023 | CS-2.5（RL7 数值唯一经注册表） |
| K-3 | 冻结 | C-03 | C-13/C-14/C-16/C-23 | F-062、F-063（→F-070~F-074、F-095） | RL6（动作无旁路） |
| K-4 | 草案 | C-15 | C-13、C-16、C-27 | F-077 | RL4（盲测纪律） |
| K-6（管线端口） | 草案 | C-13（ILlmTransport→C-16） | C-14、C-22、C-26 | F-065、F-079 | RL6/RL10（K-5 卡片结构滚动冻结于 W3） |
| K-7 | 冻结 | C-04 | C-19、C-20、C-21 | F-160 | R-E2（双后端等价，D-32） |
| K-8 | 冻结 | C-01（接线）/C-13（生产） | C-25、C-30（回放工具） | F-069（→F-121、F-174） | RL10（禁 console 冒充战报） |

## 四、变更流程（契约纪律；03 §6 + 07 D-21/D-28）

改任何一份契约（含草案级）必须走全流程，缺一不可：

1. **独立任务**：契约变更=独立任务+独立分支（`task/<C-ID>-<F-ID>-<slug>`），禁止与实现搭车提交。
2. **评审**：code-review 双轴必过且 0 BLOCKER/0 MAJOR；契约类 PR **100% 人工评审**（D-28）。
3. **版本递增**：本文件头部版本号 +1 并在变更记录注明来源；受影响契约文件首行冻结注记升级（`draft`→`frozen` 或 `frozen (vN)`）。
4. **全泳道通知**：按 §三映射表通知全部消费泳道并确认适配；每周契约漂移检查（契约文件 diff 为零或已过评审）。
5. **编译与红线复验**：`dotnet build game/EraTD.sln` 零警告零错误 + `python tools/ci/redline_scan.py` 0 FAIL + `dotnet test` 全绿。
6. **归档**：审查记录入 `docs/engineering/reviews/REV-<日期>-<任务ID>.md`，全轮次留档。

## 变更记录

- v1.0（2026-10-13）：T-019~T-025 首次成包：K-1/K-2/K-3/K-7/K-8 冻结，K-4/K-6 草案（07 D-21）；33 个纯契约文件（interface/enum/record，零方法实现）落于 `game/src/EraCore/Contracts/` 七子目录；T2 豁免目录级登记于 `tools/ci/allowlist.txt`。
