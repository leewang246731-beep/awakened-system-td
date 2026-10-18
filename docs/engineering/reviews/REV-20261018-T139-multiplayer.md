# REV-20261018-T139 — 联机模式实现（listen server+快照 200ms+系统动作服务器审批）

| 项 | 内容 |
|---|---|
| 任务 | T-139（08 v1.22 行 217；W8 批 6；L 级；验收=**listen server+快照 200ms+系统动作服务器审批**） |
| 特性 | F-161 联机架构预留 → 本任务为**实现域**（48 号 §8 移交清单逐项落地） |
| 交付物 | 新程序集 `game/src/EraNet/`（8 文件）；EraCore 唯一改动=SimCommandQueue 观测缝；表现层 `game/presentation/EraTD/scripts/net/`（3 文件：ENet 适配器+线帧格式+联机 UI）；测试 8 文件 43 用例；Main.cs 一行挂载；本 REV |
| 分批 | 3 commit（PR① 传输+接口实现 → PR② 宿主权威+对账+重连 → PR③ 审批面+EraTD+验收），message 首行均含 T-139 |
| 门槛 | run_all 六步全绿（§六 实录：G1 6741/6741、G2 9 检查 0 FAIL、G2.5 641 文件 0 拒、G2.6 0 FAIL、G3 零警告、G4 SMOKE_OK+NET_OK） |

## 一、验收词逐项对账（Spec 轴）

| 验收词 | 落地 | 证据 |
|---|---|---|
| listen server | `NetGameHost`：宿主进程=唯一权威 Simulation+唯一 SimCommandQueue 排序权+（可选）唯一 AgentPipeline；宿主本地命令 `SubmitLocal` 与客户端上行走同一盖章/入队/广播环路（listen 语义） | NetGameHostAuthorityTests 8 用例（盖章/同路/广播序=入队序/离线广播丢弃权威照跑） |
| 快照 200ms | `NetTiming.SnapshotCadenceTicks = TicksPerSecond×200/1000 = 6`（公式派生非手填）；宿主每 6 tick Publish 一行 `SimHashSample`（网格对齐——发布的每个 tick 都是客户端也自采样的 tick，命中=真比对）；客户端同节奏自采样延迟对账 | NetGameClientReconcileTests：60 tick 逐行全中（MatchedDigests=10/Skipped=0）+摘要丢弃存活（尽力而为语义） |
| 系统动作服务器审批 | 军师订单**文本**上行（`IAdvisorOrderChannel`，本任务自有缝）→宿主 K-5 五级管线 Handle/Confirm/Refuse（ApprovalGate+SystemPointLedger 只在宿主结算）→执行产生的 PlayerBuild 命令经队列 tap 随广播流下发→结论摘要下行 | NetAdvisorApprovalTests 5 用例（ask→confirm 全链落塔/Refuse 零执行/无管线诚实拒绝/澄清零命令/请求关联） |
| 两实例联机跑一局到 Victory | NetTwoInstanceVictoryTests：宿主生产 LevelSession（波系/波界检查点/胜利谓词）+客户端镜像，内存回环联机全关 | `NET_VICTORY threat=0.6 winWaves=16 victoryTick=91849 digests=15308 hash=6426683728692766638 towers=2 buildings=7`（15308=91849/6 逐行全中；两端终局哈希全等；命令流全应用零拒收） |
| 断线重连=检查点重放 | `NetSessionSnapshot`（seed+广播命令流+tick，K-7 记录形状）+`RestoreFrom` 确定性重放重建；偏差重同步同路径 | Reconnect 用例：断线 12 tick 宿主照跑→重连快照追平→**与不间断双子终态哈希全等**（RecoveryEquivalence 网络化）；Resync 用例：污染→偏差红→快照重建→恢复命中 |
| UI 最小面 | `NetMenuUi`：右上「联机」入口+房间面板（建房/加入 IP+`NetLinkStateMachine` 状态驱动的房内状态+军令输入+断开）；headless 零端口冒烟打印 `NET_OK` | G4 实录：`NET_OK link=loopback frames=120 hash=7802816630002581516 towers=3 digests=20 divergences=0 advisor=ask-confirm` |
| 单机路径零耦合 | EraCore.Net 四类型冻结面**零触碰**（成员集/序数/零实现/全类型零耦合扫描全部原样绿）；唯一 Sim 改动=纯观测缝；金样六张全绿=单机哈希逐 tick 不变 | NetReservationContractTests/NetCouplingGuardTests 原样通过（G1 内）+新增 EraNetAssemblyGuardTests（程序集方向：EraCore 零反向引用 EraNet） |

## 二、架构落位（48 号 §8 移交清单裁决记录）

1. **传输实现选型=独立程序集+表现层双轨**（48 §8 两选项并用）：会话编排（NetGameHost/NetGameClient/MemoryNetMesh/线报文）落 `game/src/EraNet`（净 net8.0、零 Godot、零 System.Net、零线程零墙钟——确定性铁律在联机域内继续成立：一切节奏由显式 tick 泵驱动）；真实 ENet 传输落表现层 `EraTD/scripts/net/EnetNetLink.cs`（Godot 高层多人 API：宿主=ENetMultiplayerPeer.CreateServer listen/客户端=CreateClient；命令/摘要/军师/快照全走 Reliable 有序通道——同一有序流保证「命令先于其后的摘要」到达）。
2. **宿主循环装配=tick 驱动铁律**（48 §1.3）：宿主帧=PumpIncoming（盖戳+广播+军师审批）→权威 Step→PublishDueDigest；窗口车道经 PauseClock.TicksThisFrame（与单机 harness 同帧律）；EraNet 内无任何墙钟。
3. **SimCommandQueue 观测缝（唯一 Sim 侧改动，本 REV 登记）**：`Enqueued` 事件在成功入队后携带 (seq, command) 触发。动因：48 §2.2 论证覆盖了网络命令的入队路径，但 §4.2 要求「审批通过的动作随广播流下发」——管线执行（PipelineExecutor.EnqueueAndApply）在宿主进程内部直接 Enqueue，不经任何可拦截的入口；观测缝让宿主按权威入队序镜像**一切**命令（表现输入/盖章上行/审批动作）进广播期刊，是「宿主=唯一排序权威」的可执行形式。纪律：纯观察者契约（禁突变/禁抛）；单机零订阅=行为逐位不变（SimCommandQueueTapTests 无订阅对照断言）；哈希域不含队列（金样全绿实证）。
4. **军师订单文本承载通道=自建第四面**（48 §8 明示留给 T-139）：`IAdvisorOrderChannel`（status+上行 Submit/DrainRequests+下行 Push/DrainReplies）+`NetHostWire`（宿主端命令面：DrainSubmissions/Broadcast——预留端口 ICommandTransport 冻结为客户端视图，宿主端线需要收发两件）。两面均在 EraNet，形状镜像冻结端口；成员集随本 REV 冻结。载荷纪律：上行只有文本与 pendingId（伪造动作 JSON 无消费路径）；下行只有结论摘要（sim 真相走命令广播流）。
5. **内容指纹门禁（48 §6.5 最小接线）**：`NetHandshakeInfo`（协议版本 1+核心版本+内容 FNV-1a 指纹）客户端上行，宿主 `Matches` 三字段全等才回快照，不一致 DisconnectPeer；指纹=关卡装配文件（towers/enemies.json）顺序敏感聚合，纯函数零 I/O。

## 三、v1 简化裁定（48 号登记项的落地口径）

1. **客户端无预测**（48 §2.3）：上行=建议+本地回显列表（SubmittedEcho）；镜像唯一状态源=广播流。英雄乐观预测+偏差回滚维持「可选增强」登记，v1 未实现。
2. **偏差处置=介质已备+策略外置**（48 §3.3）：偏差→台账记录（审计面，重同步不抹历史）→调用方决策（测试/G4 用快照重同步路径；UI 显示偏差计数）。自动重试上限/超时参数未定，登记遗留。
3. **重连会话身份**（48 §6.2）：v1 内存回环=宿主快照全量重放追平（加入=读档+追进度同一机制）；ENet 端=客户端连接后自动收快照恢复。会话身份 token/重连窗口未做（单客户端房间 v1）。
4. **联机存档归属**（48 §6.6）：未裁决——v1 联机对局不做战役进度闭环（NetTwoInstanceVictoryTests 的 LevelSession 波界检查点写内存 port，命令流经网络道不经 session._issued，**中途检查点快照在联机下不完整**——已登记为已知边界，宿主掉线=房间消亡与 48 §6.2 口径一致）。

## 四、测试面（8 文件 43 用例；五级覆盖）

| 文件 | 用例 | 级 |
|---|---|---|
| Sim/SimCommandQueueTapTests.cs | 4 | 单元：tap 序=入队序/无订阅行为不变/出队全序不受观察者影响/拒收不触发 |
| Net/MemoryNetMeshTests.cs | 9 | 逻辑：回环网全生命周期/双向 FIFO/摘要序/离线静默丢弃/丢弃注入/军师关联/宿主无上行/多客户端广播面 |
| Net/NetWireMessagesTests.cs | 7 | 单元：快照/命令/军师线格式往返（snake_case 锚）/指纹三敏感/握手三字段/协议版本冻结/节拍公式 |
| Net/NetGameHostAuthorityTests.cs | 8 | 矩阵：盖章/迟到合法/重复幂等（零命令双子对照）/伪造 SystemAction+TimeControl 无消费路径（kind-unsupported+双子哈希零污染）/广播序/listen 同路/离线权威照跑 |
| Net/NetGameClientReconcileTests.cs | 6 | 逻辑：60 tick 逐行全中/丢弃存活/篡改即红/重同步恢复/断线重连双子全等/离线零副作用 |
| Net/NetAdvisorApprovalTests.cs | 5 | 集成：ask→confirm 全链（宿主落塔+镜像经广播同步落塔+系统点宿主结算）/Refuse/无管线拒绝/澄清零命令/请求关联 |
| Net/NetTwoInstanceVictoryTests.cs | 1 | 链路：两实例内存联机全关到 Victory+哈希对账全等（CheckpointHeavyCollection 串行） |
| Architecture/EraNetAssemblyGuardTests.cs | 3 | 架构：EraCore 零反向引用 EraNet/方向非空转证明/D-01 禁依赖清单扩至 EraNet |

## 五、反作弊面复核（48 §2.4 口径）

- 非法命令在宿主 ApplyOne 处被拒且拒绝不变异：伪造 SystemAction/TimeControl 类命令经盖章→广播→两端同拒→与零命令双子哈希全等（NetGameHostAuthorityTests.Forged_non_player_command_kinds_have_no_consumption_path）。
- 19 封闭动词+K-3 Schema+系统点预算只在宿主进程内生效（CS-2.4/RL6 在宿主成立即全房间成立）。
- 军师侧：客户端上行只有文本；动作 JSON 只在宿主经 Schema+审批+扣点后存在；无管线宿主诚实拒绝。
- 残余（登记不解决，同 48 §6.3）：镜像 sim 全量状态=信息类作弊面在免费 P2P 拓扑内不可根除。

## 六、完成门槛验证（run_all 实录，2026-10-18，总体 PASS）

```
G2   红线零违规 : PASS  9 checks | 0 FAIL | 0 WARN | exit_code=0
G2.5 资产规格校验 : PASS  files=641 rejected=0 warned=0
G2.6 模组离线校验 : PASS  0 FAIL, 3 mods loaded (8 content documents)
G1   测试全绿   : PASS  dotnet test: 6741 passed, 0 failed (1161s)
G3   构建零警告 : PASS  Release 构建成功，TreatWarningsAsErrors=true（零警告，45s）
G4   冒烟通过   : PASS  SMOKE_OK 已捕获（运行时/程序集/EraCore 链路三合一，22s）
run_all: 总体 PASS（G1~G4 全 PASS）  EXIT=0
```

- G4 同跑联机冒烟行（独立复跑钉证）：`NET_OK link=loopback frames=120 hash=7802816630002581516 towers=3 digests=20 divergences=0 advisor=ask-confirm`；G4 不真开网络（内存回环承载，纪律达成）。
- 金样六张在 G1 内全绿=队列观测缝引入后单机哈希逐 tick 不变（AGENTS 纪律 9 实证）。
- 前置失败实录（诚实记录）：run#1 G4 红——本任务缺陷：NetDemoLevel.DeleteJournal 在 JsonlJournalWriter 未 Dispose 时删文件（IOException 中断 _Ready→SMOKE_OK 未打印）；修复=先 Dispose 后删除+冒烟后清理 GUID 日志，终录全绿。另：开发期间两次环境争用（并行代理的 testhost 锁 Debug 输出目录；Godot 冒烟撞 t055 战报句柄）均按先例诊断绕行（Release 配置隔离跑批次/复跑即绿），非代码缺陷。

## 七、并行实录

W8 批 6=T-139+T-141 双代理并行（08 纪律 ≤2）。T-141 中途入库（de178b0：触摸原型+MobileViewportPolicy）与本任务文件面零重叠（其触 BuildInteractionUi.cs，本任务触 Main.cs 一行/net/ 新目录/EraNet/EraCore.Tests/Net/）；6741 用例为双方合并树口径。

## 八、提交与纪律自查

- 3 commit（PR①/②/③），message 首行含 T-139；`git add` 逐路径（无 -A）；未动 08/AGENTS/config/；金样零触碰（无 T8 面）；数值零新增锚点（6 tick=公式派生；端口 23139=夹具旋钮非内容锚点）。
- 红线自查：R9（零盘符路径——obj/bin 生成物不在扫描面）/T1（零残留标记，已扫）/T2（EraCore 唯一改动文件有对应测试；EraNet 不在 T2 扫描域）/R7（EraNet 不在 R7 扫描域且无内容锚点值）/T8（未触金样）。
- CS-1.3 解释登记：「真实网络调用仅存在于测试程序集」条款成文于联机预留前；本任务任务卡明文「Godot 高层多人 API（ENetMultiplayerPeer）」落表现层——EraCore/EraNet 仍零直连网络（EraNetAssemblyGuardTests 钉死），表现层经 Godot 引擎自带多人 API 组网，无 System.Net.Http 直接引用。

## 九、遗留（诚实清单）

1. **ENet 真实 socket 路径未经自动化验证**：G4 纪律=不真开端口；EnetNetLink 的建房/加入/断线信号/字节路由只有编译（G3）+headless 冒烟的封包解析面（InjectReceivedForSmoke 走真实 HandlePacket）覆盖，真实双进程联机待窗口人工验证（或后续任务补 opt-in 自测脚本）。
2. NAT 穿透/房间发现（48 §6.1）：未做，免费定位下限定直连/同网段（v1 口径=输入 IP 直连）。
3. 断线重连会话身份/重连窗口/宿主迁移（48 §6.2）：宿主掉线=房间消亡；无身份 token（v1 单客户端房间）。
4. 跨平台浮点一致性（48 §6.4）：win64/linux64 回放已由 CI 证；ARM/Web 配对未测（兜底=同平台配对）。
5. 联机对局的战役记账/存档归属（48 §6.6）：未裁决；联机下 LevelSession 波界检查点快照不完整（命令流走网络道，§三.4）。
6. 军师多实例成本（48 §6.7）：宿主侧管线随人数线性（v1 单客户端无感）；BYOK 客户端本地 L1 建议未接 UI（L1IntentParser 客户端本地渲染属后续 UI 任务）。
7. 客户端渲染集成：联机最小面=状态面板+镜像 sim 运行（哈希对账驱动）；客户端视口渲染走 SimSnapshotBridge 的接线与主菜单场景（Wave 4 打包任务）同属后续表现层任务。
8. 窗口车道客户端步进=每帧 1 tick 的近似跟进（对账为延迟比较不误报）；正式节奏控制（TimeControl 命令经宿主盖章广播——机制已备未接 UI）。
