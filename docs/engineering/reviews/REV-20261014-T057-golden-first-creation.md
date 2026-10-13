# REV-20261014-T057-golden-first-creation — T-057 首版金样登记（CS-6.2 首版条款）

| 项 | 内容 |
|---|---|
| 登记对象 | commit `3a92a16`（"T-057: C-04 wave checkpoints + failure tiers + recovery equivalence"）——首版金样 `game/content/golden/checkpoint-e1-golden.json` 与产生它的 C-04 检查点引擎同 commit 合入 |
| 登记日期 | 2026-10-14 |
| 条款依据 | CODE_STANDARDS CS-6.2 v1.1 首版条款（先例 d6f5862/T-040，裁决见 REV-20261013-wave1-round3 处置表 #1）；CS-6.3 依据号已入 commit message（N7 / 07 D-11 / v3.2 §3） |
| 引擎触面 | `game/src/EraCore/Checkpoint/` 5 文件（WaveSnapshotRecord/WaveCheckpointStore/FailureTiers/ExerciseMode/RecoveryEquivalence），全部为本 commit 新增——引擎先于金样存在的首版因果成立（检查点恢复引擎即金样产生器） |
| 豁免登记 | `tools/ci/allowlist.txt` 增 `T8: 3a92a16` 条目（本 REV 即登记处；更新金样无豁免通道，必须独立 commit） |

## 一、金样内容与验证

- 场景：E1 夹具（t=1.2，检查点波 16=⌊22×0.75⌋）跑至波 17 边界（tick 39960），从 `#ckpt` 槽确定性重放（seed+命令流→检查点 tick 37800）后续跑 1 波；采样步长 2160（=波间隔 72s@30Hz），19 个采样由真实引擎产出（CS-4.4：era1 内容 JSON 驱动）。
- 生成入口：`ERA_CKPT_GOLDEN_UPDATE=1 dotnet test --filter FullyQualifiedName~EraCore.Tests.Checkpoint`（已留档于金样文件头 regen_command；行内容禁手改 CS-6.1）。
- 生成即验证：WriteGolden 落盘后回读并重放复现全部采样（CheckpointGoldenTests.WriteGolden 自检）；每次常规运行同时复证"恢复世界 vs 从未中断连续跑"逐采样相等（RunCheckpointRestoreEngine 内嵌断言）。

## 二、完成门槛验证（真实执行，摘录）

- `dotnet test --filter EraCore.Tests.Checkpoint`：**34/34 通过**（单元/逻辑/集成/金样四层）。
- 恢复等价主断言：第 30 波边界 kill（t=1.2，检查点 16→重放 14 波，kill tick=BoundaryTickOf(30)），恢复世界与连续跑世界在全部 70201 个比较 tick（含重放前缀与 kill 后续跑）StateHash 相等（Verified=true，FirstDivergenceTick=null）。
- 崩溃注入（复用 T-041 注入手法）：外部连接楔住写锁使波界写存档抛 SqliteException → 恢复走上一完整检查点（波 16 重放哈希=活体），滚动上一完整代（波 18）存活，楔行未落盘。
- `python tools/ci/redline_scan.py`：9 检查 0 FAIL 0 WARN（T3 类级关联 100%）。
- 全仓 `bash tools/ci/run_all.sh`：见任务回传记录（G1 含并行泳道全量测试）。

## 三、遗留与挂账

- 无新增 DEBT；"60s 局在第 30 波"任务卡口径内部矛盾（波间隔公式冻结下第 30 波≈38 分钟模拟时间）已按"测试墙钟预算≤60s"口径落注释（RecoveryEquivalenceTests 主断言方法注释），如后续裁定另有口径由 C-12 编排任务（T-059）重取。
