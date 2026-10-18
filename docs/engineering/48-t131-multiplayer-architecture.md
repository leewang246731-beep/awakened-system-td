# 48 · T-131 联机架构预留评审（F-161）

版本：v1.0；日期：2026-10-18；状态：已归档（评审结论与接口桩；实现域 = T-139）。

任务：T-131「联机架构预留评审」，任务卡 08 v1.15 行 204，验收原文「接口桩+单机路径零耦合结论归档」。
特性：F-161 联机架构预留，01-feature-inventory F-161 行（v2.1 §9 基线）。
下游：T-139「联机模式实现」，任务卡 08 行 217，L 级；其验收词「listen server+快照 200ms+系统动作服务器审批」是 T-139 的实现域，不在本文范围。本任务只做预留评审。

## §0 结论速览（五条）

1. 命令同步胜出，且几乎是免费的：本作确定性核心（命令流+状态哈希+同 seed 重放）是全仓库唯一权威架构资产；命令同步让联机直接复用存档/检查点/回放/金样全部既有不变式（§1）。
2. listen server 与 SimCommandQueue 同构：宿主是唯一排序权威，客户端镜像 sim 只消费广播流；迟到命令在队列语义里本来就是合法的——网络抖动容忍是既有语义（§2）。
3. 快照 200ms 不破 K 契约：摘要行=回放哈希时间线的一行（SimHashSample）；重连与迟到加入=检查点记录+命令尾重放；全部是既有体系的网络搬运，零新快照概念（§3）。
4. 系统动作审批零新缝：五级管线（含 F.4 审批门与系统点账本）整体活在宿主进程即可；客户端军师=本地 L1 语法+建议上行，执行权在宿主；玩家与系统同一条 BuildPlan 路径=联机下无特权路径（§4）。
5. 单机零耦合成立且被钉死：EraCore/Net 四类型（两纯契约+状态枚举+状态机），EraCore 内零实现零引用（架构守卫断言编译后程序集内无任何非 Net 类型引用 Net 类型）；金样哈希与 run_all 六步全绿实证（§5）。NAT/断线重连/作弊面如实列出，本文不解决（§6）。

## §1 确定性核心对联机的天然支撑面盘点（命令同步 vs 状态同步）

### §1.1 既有资产清单

- SimCommandQueue 全序命令入口：`game/src/EraCore/Sim/SimCommandQueue.cs`。一切状态变更只能经命令入队，天然的网络命令入口（§2.2 证明）。
- SimCommand 封闭动词记录：`game/src/EraCore/Contracts/Sim/SimCommand.cs`。命令流是重放不变式的一半（同 seed+同命令流⇒同哈希）；联机只需传输命令记录，无需传输实体状态。
- ComputeStateHash 全态 FNV-1 哈希：`game/src/EraCore/Sim/Simulation.cs`。逐帧可复算摘要=免费的偏差探测器。
- 回放校验器：`game/src/EraCore/Sim/SimulationReplay.cs`。命令流进、哈希时间线出的完整对账工具；digest 的类型来源（SimHashSample）。
- 检查点：`game/src/EraCore/Checkpoint/WaveCheckpointStore.cs` 与 `WaveSnapshotRecord.cs`。快照=（seed+命令流），恢复=重放——迟到加入/断线重连的现成协议载荷。
- 命令尾恢复等价：`game/src/EraCore/Checkpoint/RecoveryEquivalence.cs` 与 WaveTailRecord。重连后从检查点续跑的等价性是已验证的机器面。
- 拒绝不变异：`game/src/EraCore/Sim/SimCommandQueue.cs`（SimCommandResult 备注）：被拒命令从不改变 sim 状态——接收端对任何非法输入保持状态不变（§2.4 反作弊面）。
- 军师五级管线：`game/src/EraCore/Agent/Pipeline/AgentPipeline.cs`；审批门 `Agent/Governance/ApprovalGate.cs`；19 条封闭动作 `Agent/Actions/ActionCatalog.cs`。系统动作服务器审批的现成管线（§4）。
- K-8 JSONL 日志：`game/src/EraCore/Contracts/Journal/`（IJournalWriter/JournalEvent）：命令流与系统动作全量日志——网络对账的证据通道。

### §1.2 选型论证：命令同步，不选状态同步

- 经济性：命令同步每客户端仅上行命令 JSON（百字节量级）、下行广播流（同量级）；状态同步需逐帧序列化全部实体（E4/E5 关卡数百实体+弹影+载具）。对免费定位（无专用服务器带宽）+listen server 拓扑，命令同步是唯一务实选择。
- 架构成本：状态同步需要「活状态序列化」，而本仓库刻意从未实现过——恢复永远走重放（WaveCheckpointStore 类注释原文：a restore REPLAYS it on a fresh simulation instead of deserializing live state）。引入状态同步等于在 6600+ 测试与 6 张金样守护的世界旁边再立一套平行真相源。
- 免费获得的不变式：命令同步下「对账」就是既有回放校验（SimulationReplay.Verify）；「偏差检测」就是逐 digest 比对 StateHash；「重连」就是既有检查点+命令尾重放。状态同步则全部需要新机制。
- 金样护城河：game/content/golden/ 六张金样（sim-e1/sim-e2/wave-e2/checkpoint-e1/regression-v1/regression-e3-v1）锁定「同 seed+同命令流⇒同哈希」——任何破坏确定性的联机改动会直接红，无需新增护栏。

### §1.3 必须继承的确定性纪律（学费已缴）

- 同 seed 哈希是逐 tick 不变式：任何 Sim 改动保持「同 seed+命令流→哈希全等」（AGENTS 纪律 9）。联机改动同受此纪律约束——镜像端与宿主端是同一份代码、同一哈希。
- 迟到命令合法：SimCommandQueue 语义「late commands are legal — replay journals can interleave freely」（类注释原文）。网络乱序/迟到在队列语义内天然容忍（§2.3）。
- 时间必须走 tick 域：T-086 教训——流场切片从墙钟预算改为配额制（FlowFieldManager.DefaultCellsPerTick），因为墙钟会让同 seed 哈希随负载漂移。T-139 的宿主循环必须 tick 驱动，禁止用真实时间驱动 sim。
- 内容版本必须一致：金样锁定 game/content 数值表；对端不同内容/不同代码版本必然哈希漂移→房间门禁必须校验内容指纹（§6 风险清单）。

## §2 listen server 拓扑（宿主=权威 sim，客户端发命令流）

### §2.1 角色划分

- 宿主（listen server）：玩家一的进程。跑唯一权威 Simulation、唯一 AgentPipeline（含审批门与系统点账本）、唯一 SimCommandQueue 排序权。宿主本地玩家与其他客户端同权——宿主的输入也走同一 Submit/盖章/广播环路（listen 语义：宿主「听着」自己的服务器）。
- 客户端：跑镜像 Simulation（同 seed、同命令流、逐 tick 同哈希）。上行的只有命令建议（SimCommand 记录）与军师订单文本（§4）；下行消费广播命令流+摘要。
- 无专用服务器：免费定位（F-161 优先级最低的既有口径），无中心大厅假设；房间=宿主进程。

### §2.2 SimCommandQueue 即天然接口（本文核心论证）

评审结论：Simulation 不需要任何新缝。理由链：

1. 命令入口唯一：表现层与系统动作进入确定性核心只经 SimCommandQueue（03-decomposition §1.2 / K-6 SimCommand 注释原文「the ONLY channel」）。网络层只是又一路表现者——它把远端玩家的命令流送进同一个队列。
2. 排序键已为网络设计好：队列按（IssuedAtTick, Seq）全序；Seq 是本地单调发号。listen server 下唯一的发号权威是宿主：客户端 Submit 上行→宿主收到后盖 IssuedAtTick（宿主接收 tick+1）并入队→广播→所有端（含宿主本地镜像消费路径）按广播顺序 Enqueue→各端 Seq 序列一致→同 tick 内应用顺序一致→哈希一致。
3. 往返即回放：客户端消费广播流与本机单机消费本地输入是同一条代码路径（Enqueue→Step）；「网络对账」与「回放校验」是同一个操作——不需要为联机新增第二条入队路径。
4. 因此 EraCore 侧唯一缺的是「命令如何跨进程移动」的端口——即本文预留的 ICommandTransport（§7 桩面），而不是 Sim 侧任何改动。

### §2.3 时序与延迟（设计判断，非实现承诺）

- tick 域：30 Hz（Simulation.TicksPerSecond=30，K-1）。输入延迟≈单程 RTT+1 tick（盖章下一条）。塔防操作（建造/指派/升级）对 100~200 ms 延迟不敏感——本品类天然适配命令同步。
- 英雄直控（F-161 验收词「英雄预测」）是唯一低延迟敏感面：hero-order 动词已走命令流（Simulation.ApplyHeroOrder）；客户端本地乐观预测+偏差回滚（用既有检查点重放做回滚介质）是 T-139 的可选增强——本文只登记该决策点，不预留缝（避免早造抽象）。
- TimeControl（暂停/倍速）也是命令：经宿主盖章广播后，各端暂停一致、哈希一致。

### §2.4 反作弊面（命令同步下的边界）

- 宿主权威：客户端上行只是「建议」——非法命令在宿主 ApplyOne 处被拒（拒绝不变异，哈希不受污染）；19 封闭动词+K-3 Schema 校验+预算扣点（系统点只在宿主账本结算，F-161 验收词「系统点服务器侧结算」）在宿主侧全部生效。
- 无旁路：CS-2.4（动作唯一经注册表）+RL6 在宿主进程内成立即全房间成立。
- 残余作弊面（信息类）：镜像 sim 架构下客户端持有全量状态——透视类信息作弊无法在纯 P2P 免费拓扑内根除；诚实登记为 §6 遗留，不在本任务解决。

## §3 快照 200ms（K 契约不破——快照=检查点既有体系的网络化）

### §3.1 200ms 摘要通道

- 换算：30 Hz 下 200 ms=6 tick（30 tick/s × 0.2 s=6 tick，公式含整除守恒：6 tick 整数化无舍入）。
- 摘要载荷=SimHashSample（tick+StateHash）：即回放哈希时间线的一行（SimulationReplay.Capture 的采样行）。宿主每 6 tick Publish 一行；客户端在本端到达同 tick 后复算 ComputeStateHash 比对——不一致即偏差告警（对账介质=既有哈希，无新概念）。
- K 契约不破的论证：SimSnapshot（K-6 渲染视图，双缓冲已存在）、哈希时间线（K-6/回放体系）、WaveSnapshotRecord（K-7 检查点）三者全部既有；网络层只搬运既有记录，不新增任何快照概念，故 K-6/K-7 零变更（预留面 ISnapshotChannel 载荷即复用 SimHashSample，见 §7）。
- 摘要是尽力而为的：掉一行不致命（下一行 200 ms 后再来）；因此预留接口定义 Publish 在非 Online 态静默丢弃，不缓冲（见 ISnapshotChannel.cs 契约注释）。

### §3.2 迟到加入与断线重连=既有检查点体系

- 加入/重连协议（T-139 实现域，本文只证明介质已存在）：宿主下发 WaveSnapshotRecord（seed+边界命令流，K-7 槽位内容即此格式）→新端 RestoredWaveSnapshot.Replay 重建至边界→续放命令尾（WaveTailRecord 语义）至当前 tick→进入摘要对账。全链路组件今日全部存在（WaveCheckpointStore/RestoredWaveSnapshot/RecoveryEquivalence 已被测试覆盖）。
- 与单机读档同构：联机「加入房间」在数据面就是一次「读档+追进度」——因为存档本来就是（seed+命令流）。这是命令同步选型的直接红利。
- 快照节奏与检查点节奏解耦：检查点按波边界（WaveCheckpointStore 槽位轮转）；网络摘要按 6 tick 匀速。两者共享记录格式、互不依赖。

### §3.3 哈希偏差的处置（T-139 决策域，本文登记选项）

- 检测到偏差：客户端可请求宿主重发检查点+命令尾做本地重同步（同 §3.2 路径）；或宣告失步退出对局。处置策略、重试上限、超时参数全部留给 T-139——本文不预置状态（避免早造抽象）。

## §4 系统动作服务器审批（19 封闭动作在宿主侧 K-5 管线审批）

### §4.1 现状盘点：管线本身就是审批机

- 五级管线（T-053/C-13，F-065）：军令文本→L1IntentParser（意图）→IntentRouter（路由）→PipelinePlanner（多步草案：动作 JSON+系统点+资源+风险红标）→ApprovalGate（allow 直过/ask 产方案卡挂起等玩家/deny 拒）→PipelineExecutor（注册表执行）→AgentReporter（汇报）。全程 K-8 JSONL 落盘。
- ApprovalGate（K-5 实现层，F.4 矩阵）：能力锁（verb 解锁时代>当前时代→Deny）→危险位 fail-closed（不可读→Deny；RequireTrueElseDeny 缺失→Deny；真值强制 Ask）→档位（Allow 直过；Ask 仅本关白名单内 Allow）。
- SystemPointLedger：唯一点数收费者（C-14 扣点咽喉）——F-161 验收词「系统点服务器侧结算」的现成载体。
- ActionCatalog：19 条封闭动词唯一行源（recon/construction/logistics/combat/management 五组；RL6 封闭动词规则——模组可放宽参数空间，永不可扩列表）。

### §4.2 联机映射：整条管线住在宿主

- 评审结论：零新缝。AgentPipeline 是纯逻辑对象，在宿主进程内以宿主 sim+宿主账本组装即得「服务器审批」——不需要任何接口改动（管线既有构造参数全部注入式：view/tick/journal 等端口照旧）。
- 客户端军师=本地 L1 语法+建议上行：客户端跑 L1IntentParser（Agent/Grammar 纯函数组件：TextNormalizer/AliasDictionary/ChineseNumber，零网络零 I/O）做本地即时反馈（澄清卡/快捷轮盘可本地渲染）；军令订单文本上行至宿主，由宿主跑权威五级管线。
- 执行权在宿主：审批通过的动作经 PipelineExecutor 进宿主 SimCommandQueue（SystemAction 类命令，K-3 已校验 JSON），随 §2 广播流下发——客户端镜像 sim 收到的与其他命令无差别的权威命令。
- BYOK 分界（K-9）：客户端可用自己的 LLM key 生成本地建议文本（ILlmTransport 端口本来就进程内注入）；但任何「建议」只是文本——不构成动作。动作 JSON 只在宿主侧经 K-3 Schema+K-5 审批+扣点后才存在。伪造的客户端「动作 JSON」没有任何消费路径（§2.4）。
- PlayerBuild 同路径即无特权：玩家建造与系统 BuildPlan 走同一执行路径、同一 cell 上限（SimCommandKind.PlayerBuild 注释原文「via the same BuildPlan execution path as the system」）——联机下不存在「玩家直连后门」。

### §4.3 延迟面（登记，不解决）

- ask 档动作的方案卡需要玩家确认：确认也是一次上行（Confirm/Refuse）→宿主裁决→广播。一次往返的延迟对回合感塔防可接受；精确阈值实测随 T-139。
- 军师订单文本上行带宽：单条军令百字量级，忽略不计。

## §5 单机路径零耦合证明（本任务验收项）

### §5.1 证明结构（三层证据）

- 静态证据（编译期）：EraCore/Net 四类型全部为纯契约+纯状态机（零网络实现、零 Godot、零 System.Net.Http——D-01/D-05 既有守卫 GodotSharpGuardTests 管辖程序集引用面，本批全绿）。
- 结构证据（反射期，新增守卫）：NetCouplingGuardTests 扫描编译后程序集全部导出类型的成员面（基类链/接口/字段/属性/事件/方法签名，含泛型参数与私有字段），断言 EraCore.Net 之外没有任何类型引用 EraCore.Net——单机路径（Simulation/AgentPipeline/Checkpoint/表现层适配）在类型面上够不着预留面。附带负向对照：同一扫描器指向 EraCore.Sim 找到大量既有引用（证明扫描器真能发现引用，非空转）；并断言 Net→Sim 的依赖方向（预留面只引用确定性核心类型，反向为零）。
- 行为证据（运行期，全量回归）：run_all 六步全绿（G2 红线/G2.5 资产/G2.6 模组/G1 测试/G3 零警告构建/G4 Godot 冒烟）；其中金样测试（sim-e1/sim-e2/wave-e2/checkpoint-e1/regression 两张）全部通过=预留面引入后单机哈希逐 tick 不变（同 seed+同命令流仍得同哈希——AGENTS 纪律 9 的实证）。

### §5.2 为什么零耦合在构造上成立（论证，非仅测试）

- 没有任何调用方：预留面在 EraCore 内零引用（§5.1 结构证据）——单机运行路径根本不构造 Net 类型；未被构造的纯契约不产生任何行为。
- 接口无实现：EraCore 内 ICommandTransport/ISnapshotChannel 零实现（NetReservationContractTests 钉死）——不存在半截网络逻辑混入逻辑库的可能。
- 依赖单向：Net→Sim（命令与摘要载荷是确定性核心的既有类型），Sim 不知 Net——预留面是「叶子」，不是「根」。

## §6 风险与遗留（诚实列出，不解决）

1. NAT 穿透：listen server=宿主在消费级 NAT 后；UDP 打洞/中继/房间发现（Steam/itch 无中心服务假设）全部未决——免费定位下无专用中继预算。T-139 立项时须先裁决穿透方案（或限定直连/同网段场景）。
2. 断线重连与会话：介质已备（§3.2），但会话身份、重连窗口、超时裁决、以及宿主掉线=房间消亡（无宿主迁移——活状态序列化按架构不存在）未决。宿主迁移明确超出范围：那需要打破「恢复=重放」原则。
3. 作弊面残余：信息类作弊（镜像 sim 全量状态）无法在免费 P2P 拓扑根除；身份验证/黑名单无账号体系可用；哈希对账能发现偏差但无法强制作弊端服从。T-139 须先写威胁模型再定对抗预算。
4. 跨平台浮点一致性：确定性哈希假定对端浮点行为逐位一致。本仓库 win64/linux64 的回放已被 CI 证明；但 ARM/移动端/Web（mono 与 coreclr 差异）未测——跨平台配对是 T-139 的实测项（兜底：同平台配对）。
5. 内容指纹门禁：对端模组/内容版本不一致必然哈希漂移；房间加入须校验（game/content+mods 指纹）。mod_validate 工具已存在，接线归 T-139。
6. 联机存档归属：K-7 存档是单机形状（宿主存=谁的战役进度？）；联机对局的战役记账（若结算进战役）未裁决。
7. 军师多实例成本：每客户端 BYOK 本地建议+宿主权威管线=文本上行带宽可忽略，但宿主侧 LLM 调用（若宿主也跑军师）随人数线性增长——宿主算力/配额上限未定。

## §7 预留面清单（本任务交付的桩面）

### §7.1 产品面（game/src/EraCore/Net/，四类型两文件一状态机）

- ICommandTransport（`Net/ICommandTransport.cs`，T2 纯契约）：命令传输端口。Status（NetLinkStatus）+Submit 上行（仅 Online 态受理；false=链路态拒绝或背压，零异常零变异）+DrainIncoming 下行（宿主广播序）。载荷=SimCommand 记录（K-6 既有）。
- NetLinkStatus（同文件，T2 纯契约）：Offline/Connecting/Online 三值枚举（序数 0/1/2 冻结——线协议词表）。
- ISnapshotChannel（`Net/ISnapshotChannel.cs`，T2 纯契约）：快照推送端口。Status+Publish（宿主侧；非 Online 静默丢弃——摘要尽力而为）+DrainIncoming（客户端侧，按发布序）。载荷=SimHashSample（回放哈希时间线行，既有）。
- NetLinkStateMachine（`Net/NetLinkStateMachine.cs`，行为组件）：合法迁移=Offline→Connecting→Online→Offline（含 Connecting→Offline 握手失败；Offline 上 MarkOffline=合法空操作——双 drop 回调常态）；其余迁移抛 InvalidOperationException 且状态不变；CanSubmit=仅 Online。全部传输实现共享的迁移词表。
- 纪律：接口成员集冻结为「status+上行+下行」三件（NetReservationContractTests 反射钉死——加宽须过评审）；EraCore 内零实现（同测试钉死）。

### §7.2 测试面（三文件 17 用例）

- Net/NetLinkStateMachineTests.cs（8 用例）：合法全生命周期/握手失败重连/Offline 双断空操作（boundary）/四类非法迁移（illegal，含状态不被污染断言）/异常消息可诊断性。
- Net/NetReservationContractTests.cs（6 用例）：两接口成员集冻结/枚举三值序数冻结/EraCore 零实现/状态机不实现端口/载荷类型复用断言（SimCommand+SimHashSample 签名级验证）。
- Architecture/NetCouplingGuardTests.cs（3 用例）：非 Net 类型零引用 EraCore.Net（零耦合主断言）/Sim 对照组（扫描器非空转证明）/Net→Sim 方向断言（含 Net 四类型清单校验）。

### §7.3 登记面

- tools/ci/allowlist.txt 新增两条 T2 声明（两纯契约文件；行为面=状态机测试+接口面冻结断言；实现随 T-139 落地）。

## §8 移交 T-139（实现域清单）

- 已备好：命令同步选型论证（§1）；SimCommandQueue 即天然接口论证（§2.2——Sim 侧零改动）；200ms 摘要=6 tick+SimHashSample（§3.1）；加入/重连=检查点+命令尾（§3.2）；服务器审批=管线住宿主+客户端 L1+订单文本上行（§4.2）；三端口一状态机（§7）。
- 待 T-139 裁决：传输实现选型（ENet/WebSocket/其他，落表现层或独立程序集——EraCore 永不触网 D-01）；宿主循环装配（tick 驱动铁律 §1.3）；§6 七项遗留的逐项处置；军师订单文本的承载通道（可复用命令传输或表现层信道——本任务未预留第三接口，避免早造抽象）。
- 红线继承：任何 T-139 改动不得破坏「同 seed+同命令流⇒同哈希」（金样即门禁）；不得使单机路径触网；不得绕过 19 封闭动词注册表。

---

变更记录：v1.0（2026-10-18）首版归档——T-131 评审结论+最小桩面+零耦合证明。评审记录见 docs/engineering/reviews/REV-20261018-T131-multiplayer-reservation.md。
