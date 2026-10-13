# REV-20261014-T059-campaign-loop — T-059 三笔 DEBT 处置登记（C-12 游戏循环 E1 完整+选图薄版）

| 项 | 内容 |
|---|---|
| 任务 | T-059（游戏循环状态机 E1 完整+选图薄版；验收 08：E1 一关全循环走通+中途存读档状态无损，F-001/F-005） |
| 登记日期 | 2026-10-14 |
| 出处 | 08-task-plan T-059 行尾三笔 DEBT（v1.3 追加；原出处 spike1-report §7 / spike2-report §5 / REV-20261013-wave1-round3） |
| 触面 | `game/src/EraCore/CampaignLoop/`（GameStateMachine/CampaignGrid/CampaignState/LevelSession 四件新增）+ `EraCore.Tests/CampaignLoop/` 七件新增；`Logistics/Carrier.cs` 一处最小修改（DEBT ② 事件，见下） |

## 一、DEBT ①（spike2-pit#1）：单位层无碰撞=逻辑层语义正式声明

- **声明内容**：敌/民夫（及一切单位）在逻辑层**互不阻塞、同格自由叠加通过**是既定设计语义，不是实现缺口；T-059 全循环（守波推进、场清判定、民夫物流、波界存档重放）的验收全部建立在该语义之上。
- **登记位置**：`EraCore/CampaignLoop/LevelSession.cs` 类文档注释（CampaignLoop 层正式声明段）+ 本 REV；测试侧 `CampaignLoopE2ETests` 类注释同源引用。
- **观感债去向**：挤压/排队**观感**（同格叠影、聚团、门口排队演出）归 **C-21 表现层**——登记到 **T-07x 表现层任务行**（T-075~T-077 D 泳道表现层批次；渲染侧落地随 C-21 渲染三档 T-102 一并考虑）；**局部避让规则**（玩法层的推移/绕行）为后续独立任务，不属 T-059。逻辑层在 T-059 交付中不含也不等待任何单位间碰撞。
- **为何在此声明**：spike2 报告 pit#1 原文要求"验收'互不阻塞'时必须先声明这一层语义"——本 REV 即该声明的正式留档。

## 二、DEBT ②（spike2-pit#5）：Carrier 路线失效正式事件

- **实现**：`Logistics/Carrier.cs` 最小修改（逐处注明 T-059 DEBT ②）——新增 `RouteInvalidatedEvent` 记录（Carrier/Phase/DepartureCell/TargetCell）与 `Carrier.RouteInvalidated` 事件，恰在 `BeginOutbound`（Loading→Outbound）与 `BeginInbound`（Unloading→Inbound）两处相位过渡、相位已置位后触发：订阅者可在事件内同步合法 `Reroute`（编排器"路线失效→重算→改道"正式缝）。
- **确定性无扰**：事件纯观测——不涉 rng/时钟/哈希态，`ComputeStateHash` 与 T-040 重放不变式不受订阅与处理影响（既有 Carrier/Logistics 确定性测试全绿复证）。
- **消费验证**：`EraCore.Tests/CampaignLoop/CarrierRouteInvalidatedTests.cs` 六例——事件恰在两次移动相位过渡触发（字段=出发格+相位目标）、编排器事件内同步改道且交付不受影响、断供等待期零事件、无订阅者零成本、服务窗中 Reroute 仍被拒（事件是唯一相位锁改道时机）、错起点/错终点改道仍被既有守卫拒绝。
- **E1 薄版接线说明**：E1 薄关卡无中途墙事件，`LevelSession` 默认不订阅（文档注明缝位留给需要墙感知改道的宿主）；spike2 的相位轮询原型由此升级为正式事件契约。

## 三、DEBT ③（spike1-pit#1 弹影层重取基准 / pit#2 哈希降频）

- **弹影层（pit#1）**：E1 薄战斗仍为 hitscan 即结算（快照 Projectiles 空数组）——**无新动作**；待 C-09 弹体演进/K-6 落地引入真实弹实体时按 spike1-report §7 口径重取性能基准（该行原文照引，债保持挂起）。
- **哈希降频（pit#2）**：`LevelSessionConfig.StateHashInterval`（默认 **1**=每 tick，金样[ sim-e1/checkpoint-e1 ]与无损恢复断言依赖，默认行为不变）提供会话层采样降频机制：>1 仅稀释 `LevelSession.HashLedger` 观测账本，`Simulation.Step` 内部逐 tick 全态哈希（Sim 层）不动。机制验证：`LevelSessionTests.Hash_ledger_sampling_cadence_is_configurable`（默认逐 tick + 间隔 30 仅倍数 tick 采样）。**评估结论挂 T-067**（千单位级场景前裁定降频/增量哈希是否安全——重放不变式与恢复等价依赖逐 tick 对照）。

## 四、验收证据（详见任务回传）

- E2E 全循环（`CampaignLoopE2ETests.E1_full_loop_menu_to_victory_and_next_cell_selection`）：选图→进关→命令流（采集/加工/民夫/双塔）→胜波达成且场清→Victory→结算入库（守恒：授予=入库+拒收）→CompleteLevel 返回 CampaignGrid→占领入库→选下一格（第二关进/玩/退/重进无损）。
- 中途存读档无损（`LevelSessionTests.Mid_wave_exit_and_reentry_is_lossless`）：波中暂停退出（level 域 `#exit` 快照 + campaign 域池文档）→重进后 **Simulation ComputeStateHash 逐位相等 + GameStateMachine 态/迁移史相等 + 资源池逐字段（逐资源量/仓库等级/容量）相等**；续跑与从未中断孪生逐采样哈希相等。
