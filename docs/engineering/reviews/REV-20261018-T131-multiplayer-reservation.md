# REV-20261018-T131 — 联机架构预留评审（接口桩+单机零耦合归档）

| 项 | 内容 |
|---|---|
| 任务 | T-131（08 v1.15 行 204；W8 批 5；S 级；验收=接口桩+单机路径零耦合结论归档） |
| 特性 | F-161 联机架构预留（01-inventory F-161 行） |
| 交付物 | 评审文档 `docs/engineering/48-t131-multiplayer-architecture.md`（§0~§8）；接口桩 `game/src/EraCore/Net/`（ICommandTransport+NetLinkStatus / ISnapshotChannel / NetLinkStateMachine）；测试三文件 17 用例；allowlist 两条 T2 声明；本 REV |
| 实现域 | T-139（08 行 217，L 级）——本任务零网络实现、零 Godot、零 Sim 改动 |

## 一、预留边界声明（本 REV 核心）

1. **本任务是评审+接口预留，不是联机实现**：08 行 204 验收原文「接口桩+单机路径零耦合结论归档」；08 行 217 的「listen server+快照 200ms+系统动作服务器审批」全部是 T-139 的实现域。本任务未写一行网络代码、未发一个网络包、未起一个线程。
2. **Simulation 零改动**：评审结论是命令流入口既有 SimCommandQueue 即天然接口（48 号 §2.2 四步论证）——sim/agent/checkpoint/presentation 全部既有目录零触碰。
3. **EraCore 零网络实现红线**：ICommandTransport/ISnapshotChannel 在 EraCore 内零实现（NetReservationContractTests 钉死「No_EraCore_type_implements_the_reserved_ports」）；D-01/D-05（禁 Godot/禁 System.Net.Http/禁 Newtonsoft）由既有 GodotSharpGuardTests 继续管辖，本批全绿。
4. **接口面冻结**：两端口成员集=「status+上行+下行」三件（反射断言钉死——静默加宽会红）；军师订单文本通道未预留第三接口（T-139 决策域，避免早造抽象——Speculative Generality 红线）。
5. **不动 08/AGENTS/config/**；金样零触碰（无 T8 面）；数值零新增（无 R7 面——200ms→6 tick 只是文档换算，未入代码常量）。

## 二、评审文档要点（48 号 §0 五条）

1. 命令同步胜出且几乎免费：确定性核心（命令流+状态哈希+同 seed 重放）复用存档/检查点/回放/金样全部既有不变式；状态同步需「活状态序列化」——本仓库刻意从未有过（恢复=重放原则）。
2. listen server 与 SimCommandQueue 同构：宿主唯一排序权威（盖 IssuedAtTick+发广播序），各端按广播序 Enqueue→Seq 一致→应用序一致→哈希一致；迟到命令在队列语义里本就合法（网络抖动容忍=既有语义）。
3. 快照 200ms 不破 K 契约：30 Hz×0.2 s=6 tick；摘要=SimHashSample（回放哈希时间线行）；迟到加入/断线重连=WaveSnapshotRecord+命令尾重放（既有检查点体系的网络搬运，K-6/K-7 零变更）。
4. 系统动作审批零新缝：五级管线（K-5 ApprovalGate F.4 矩阵+SystemPointLedger 唯一扣点）整体住宿主进程即得「服务器审批+系统点服务器侧结算」；客户端军师=本地 L1IntentParser（纯函数）+订单文本上行；伪造动作 JSON 无消费路径；玩家建造与系统走同一 BuildPlan 路径=无特权后门。
5. 单机零耦合三层证据：静态（纯契约零实现）+结构（NetCouplingGuardTests 反射扫描：非 Net 类型零引用 EraCore.Net，含 Sim 对照组证非空转+Net→Sim 单向断言）+行为（run_all 全绿含六金样=单机哈希逐 tick 不变）。

## 三、桩面清单

| 面 | 成员 | 说明 |
|---|---|---|
| 命令传输端口 | `ICommandTransport`（Status/Submit/DrainIncoming） | 上行=本地命令→宿主（仅 Online 受理，false=链路拒绝/背压零异常）；下行=宿主广播序命令流。载荷=SimCommand（K-6 既有） |
| 链路状态枚举 | `NetLinkStatus`（Offline=0/Connecting=1/Online=2） | 线协议词表，序数冻结 |
| 快照推送端口 | `ISnapshotChannel`（Status/Publish/DrainIncoming） | 宿主→客户端单向；非 Online 态 Publish 静默丢弃（摘要尽力而为）。载荷=SimHashSample（回放哈希时间线行，既有） |
| 链路状态机 | `NetLinkStateMachine` | 合法迁移 Offline→Connecting→Online→Offline（Connecting→Offline=握手失败；Offline 上 MarkOffline=合法空操作）；其余迁移抛 InvalidOperationException 且状态不变；CanSubmit=仅 Online。预留面唯一行为组件 |
| 登记面 | allowlist 两条 T2 | 两纯契约文件；行为面=状态机测试+接口面冻结断言 |

## 四、单机零耦合证据（三层）

1. 编译期：Net 四类型纯契约+纯状态机；EraCore 程序集引用面零新增（GodotSharpGuardTests 四用例全绿——含 EraTD 正向对照）。
2. 反射期（新增守卫 NetCouplingGuardTests 三用例全绿）：编译后程序集全部导出类型的成员面（基类链/接口/字段含私有/属性/事件/方法签名/泛型参数）扫描——EraCore.Net 之外零类型引用 EraCore.Net；对照组（同扫描器指向 EraCore.Sim）找到大量既有引用证明扫描器非空转；Net→Sim 方向断言（预留面只引用确定性核心，反向为零）。
3. 运行期：run_all 六步全绿（§六 实录）；其中六张金样（sim-e1/sim-e2/wave-e2/checkpoint-e1/regression-v1/regression-e3-v1）通过=预留面在树内时单机哈希逐 tick 不变。

## 五、测试面（三文件 17 用例，全部真实断言）

| 文件 | 用例 | 口径 |
|---|---|---|
| Net/NetLinkStateMachineTests.cs | 8 | legal：全生命周期+提交门/握手失败重连；boundary：Offline 双断空操作+异常消息可诊断；illegal：四类非法迁移（含状态不被污染断言） |
| Net/NetReservationContractTests.cs | 6 | 两接口成员集冻结/枚举三值序数冻结/EraCore 零实现/状态机不实现端口/载荷复用（SimCommand+SimHashSample 签名级） |
| Architecture/NetCouplingGuardTests.cs | 3 | 零引用主断言/Sim 对照组/方向断言（含 Net 四类型清单校验） |

## 六、完成门槛验证（run_all 实录，2026-10-18 安静窗，总体 PASS）

```
G2   红线零违规 : PASS  9 checks | 0 FAIL | 0 WARN | exit_code=0
G2.5 资产规格校验 : PASS  files=641 rejected=0 warned=0
G2.6 模组离线校验 : PASS  0 FAIL, 3 mods loaded (8 content documents)
G1   测试全绿   : PASS  dotnet test: 6684 passed, 0 failed (1659s)
G3   构建零警告 : PASS  Release 构建成功，TreatWarningsAsErrors=true（零警告，53s）
G4   冒烟通过   : PASS  SMOKE_OK 已捕获（运行时/程序集/EraCore 链路三合一，32s）
run_all: 总体 PASS（G1~G4 全 PASS）
```

- 合并树口径：终录为 T-131+T-140 双方全部在途交付面的合并树（6684 用例含本任务 17 个新用例与 T-140 Procedural/Endless 用例）；金样六张全绿=单机哈希逐 tick 不变的运行期证据。
- 三次前置失败实录（诚实记录，均为环境伪影非代码缺陷，链路见 §七）：run#1 G2 被 T-140 WIP 的 4 条 T2 违规暂阻（其测试后到）；run#2 G1 构建段被崩溃残留 testhost 的 DLL 文件锁（MSB3027）暂阻；run#3/#4 G1 段 Test host process crashed（2766/3737 已过 0 失败后崩溃，崩溃点漂移=并发构建覆写共享输出目录特征）。诊断佐证：`dotnet test --no-build` 隔离跑同树 6684/6684 全绿（27m37s，与对方测试并发）；终录安静窗同判据全绿。

## 七、并行实录（诚实记录，T-140 程序关卡生成器同批在仓）

1. W8 批 5 = T-131+T-140 双代理并行（08 纪律 ≤2）。T-140 的 Procedural/ WIP 中途入库（四源文件先行、测试后到）期间，本任务首次 run_all 在 G2 被 4 条 T2 违规暂阻（全部指向 Procedural/*.cs，无一属本任务触面）；待其测试文件落地后同树 G2 自愈 0 FAIL。
2. 一次全量 dotnet test 与对方树内容变更并发，出现「Test host process crashed」（6521 passed/0 failed 后崩溃）——同机共享测试宿主内容变更伪影（REV-20261018-T138 §七.2 同型先例）；安静复跑同树全绿（§六）证实为环境抖动而非代码缺陷。
3. 本任务与 T-140 文件面零重叠（本任务独占：Net/ 三文件+测试三文件+allowlist T-131 段+48 号文档+本 REV）；allowlist.txt 为共享追加式文件（各自追加自段，无行冲突）。
4. 崩溃残留连锁：上述「Test host process crashed」遗留的 testhost 进程锁住 EraCore.Tests 输出目录 EraCore.dll，使第二次 run_all 在 G1 构建 copy 段 MSB3027 失败；taskkill 清进程后诊断继续（见 §六 隔离佐证），终录安静窗全绿（§六）——诊断链完整留档，非代码缺陷。

## 八、提交

- 单 commit（S 级），message 首行含任务号 T-131：评审文档+Net 接口桩+测试三文件+allowlist 两条 T2+本 REV。
- 未动 08/AGENTS（主管记账域）；未动 config/；未动金样（无 T8 面）。
