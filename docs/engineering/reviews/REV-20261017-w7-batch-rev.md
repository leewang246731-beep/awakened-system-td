# REV：W7 批次 T-123~T-126 四任务 per-task REV 回填（2026-10-17）

| 项 | 内容 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-10-17 |
| 性质 | **回填**——四任务各自落库时（e4324f8/73f610d/3e51ba4+38fd5c7/2a59e72）未随附独立 REV 记录，本文按其 commit message 正文、任务报告文档（28 号 v1.1/41 号 v1.1/43 号/44 号相关章节）与测试树现状**一次性汇总回填**；所有事实溯源自上列档案，回填时点未重跑历史门禁（当时门禁证据=各 commit 正文所载 run_all 记录+最近邻全绿=REV-20261017-v10-fullclear §九 2026-10-17 run_all G1~G4 全 PASS 6157 passed；本回填所在 commit 的最新全绿见文末） |
| 范围 | T-123（E4 内容数据行）/T-124（E4 战役关卡+互联网奇观）/T-125（遗迹映射正式版）/T-126（四人格全量+语音）——W7 前四任务 |
| 不在范围 | T-127/T-128（各有独立报告 43/44 号）、T-130 硬验收（独立 REV-20261017-v10-fullclear）、W7 收口修复（F1~F3，随本回填同 commit，另行登记于 08 v1.15 变更记录） |

---

## 一、T-123 E4 内容数据行（commit e4324f8，2026-10-16）

| 项 | 内容 |
|---|---|
| 任务卡 | 08 行：电网/卡车/导弹/激光/雷达/夜枭/排期一致（F-008/F-012/F-016/F-092 [C-02]，schema+R，L） |
| 交付物 | `game/content/era4/` 五文件组（era/towers/enemies/recipes/levels）+ `EraCore/Sim/EnergyRiskWorldConsumer.cs`（W6 出口 P1 挂账项=风险 AOE 世界层消费，随本任务清账）+ Simulation 相位 (2) 接线 + SimulationConfig.RadiationZoneHpDecayPerSecond 内容通道 |
| 内容锚 | E4 行锚 v3.2 §4 L61：石油/电子/混凝土三资源、电网 1000/4000/20 000（核电站危险位=11.4/F.4 确认先例）、传送带 15/s+卡车并列、导弹/激光/雷达（+地雷章节设计行）、夜枭 20000/22（stealth=雷达反制钩）、13/16/19 排期；SP-4 L146 过热表全值携带+L147 熔毁全值（+辐射 HP 衰减 5/s=300/60 [NEW-T-123]） |
| 测试 | Era4ContentTests（锚点/SP-4 表/塔律/链深 3/三源矩阵+反例）+ EnergyRiskWorldConsumerTests（手算 AOE/区域断言、sim 集成 E3 tick 1619/E4 tick 2069、确定性、非法参数）——共 11 文件 2817 行 |
| 判定 | **过**——任务卡验收面（电网/卡车/导弹/激光/雷达/夜枭/排期）逐项有内容行+断言；commit 正文载 run_all G1~G4 all PASS（6012 passed） |
| 纪律核 | E1~E3 位等价：zones 仅 live 时入哈希（E1~E3 bit-identical，commit 正文）；RL7 数值全入内容 JSON（辐射衰减经 SimulationConfig 内容通道） |

## 二、T-124 E4 战役关卡+互联网奇观（commits 3e51ba4 主交付 + 38fd5c7 金样独立 commit，2026-10-17）

| 项 | 内容 |
|---|---|
| 任务卡 | 08 行：E4 全关可玩+雷达全图共享断言（F-055/F-060 [C-19,C-20]，内容校验，L） |
| 交付物 | `levels/e4.json` 首批 5 关（e4-01..03 与 era4/levels.json 冻结排期逐行互锁+L4-4 雷区后续关 era4:tower-mine 首秀+campaign:level-e4-final 夜枭终局关）；`levels/e3.json` 补 campaign:level-e3-final（堡垒号 Boss，践踏 100/45 锚）；era_transition.json final_levels[4]=campaign:level-e4-final（不再悬空，a58c86f 反例收口）；`Legacy/WonderRadarSharingBoard.cs` 互联网奇观最小通道；测试侧 era4 装载器（Era4ScheduleRows/Era4EnergyRisk/EraDir 扩展） |
| 雷达全图共享断言 | WonderRadarSharingBoard：建成互联网奇观 ⇒ 任意站位雷达覆盖全图（半径封顶=远角跨度）；未建 ⇒ 站位自身欧氏光环。内容形态守卫：radar_shared 有限分数 ≥1.0（二值通道）。WonderRadarSharingBoardTests legal/boundary/illegal |
| 可玩性证明 | 五关全 headless 清关至 Victory 零拒收（Era4LevelPlaythroughTests）；估算 15.23~16.89 min、中位 15.86（15~40 带内，Era4HandcraftedLevelPacingTests）；同 seed 双跑哈希全等；夜枭 19 波 20000×1.1667×1.5=35000 HP 手算锚 |
| 跃迁链闭环 | FinalLevelTransitionReachabilityTests：E3 五关全清+全占+世博会建成 → E3→E4 Precheck 过（排期 e4-01..03、仓库 3→4）；反例=只占非终局关仍拒 |
| 金样纪律 | 38fd5c7=regression-e3-v1 重生成独立 commit（CS-6.2：level_rotation 4→5、summary_hash→5062978767550388397，`ERA_REGRESSION_E3_GOLDEN_UPDATE=1` 入口，无 src 产品码改动）；Era3RegressionTests 21/21 复绿 |
| 判定 | **过**——3e51ba4 正文载 run_all G2/G2.5/G2.6/G1（6094 passed）/G3/G4 all PASS；38fd5c7 载 G1 复验绿 |
| 诚实登记 | commit 正文如实登记两笔：①共享文件缝（era_transition.json+LegacyContentTests.cs 同载 T-125 行+T-124 行的工作树状态）；②Spike1PerformanceTests 在 e4324f8 隔离 worktree 高载下首败一次（共享开发机），最终 run_all 内通过——性能门禁安静窗口纪律（AGENTS.md 纪律 6）的既有事实，非本任务引入 |

## 三、T-125 遗迹映射正式版（commit 73f610d，2026-10-17）

| 项 | 内容 |
|---|---|
| 任务卡 | 08 行：往返无丢失+冲突降级产品化+指标延续 G9（F-057/F-168 [C-20]，R+指标，L） |
| 交付物 | `Legacy/RelicDigSites.cs`（挖掘位生成：降级废墟地图实体化）+ `CampaignLoop/RelicContinentState.cs`（主线集成）+ `Presentation/RelicMarkerBoard.cs`（新时代地图 UI 标记决策表）+ RelicMapper/EraTransition 主线接线 + era_transition.json E3→E4 行 + `tools/art/relic_placeholders.py`（跨代遗迹标记 4 PNG 合成器，确定性无随机）+ 28 号 v1.1 §2.3/§4.4 新冻结面登记（textures/relic/ 4 件 sha256；既有 63 PNG 零触碰） |
| 测试 | E3ToE4TransitionRegressionTests（E3→E4 映射回归）+ RelicDigSitesTests + RelicContinentStateTests + RelicArtMappingTests + RelicMarkerBoardTests（+ LegacyContentTests/EraTransitionTests 扩展）——21 文件 1817 行 |
| G9 指标延续 | 往返/冲突降级面沿用 spike③ 指标口径（G9=两级存档映射冲突率，Spike3RelicMappingScenario 基线）；E2→E3（E2ToE3TransitionRegressionTests）先例在 E3→E4 同构复验 |
| 判定 | **过**（回填口径）——commit 仅一行 message、无独立报告文档；**本表按 diff 清单+28/41 号增补章节回填**。落库时点门禁=其后同日 v10 gate 的 2026-10-17 run_all 全绿（REV-20261017-v10-fullclear §九）；挖掘位=0 的链上事实已由 v10 REV §三如实登记（本链城市事实无降级废墟——挖掘位>0 面由本任务 RelicDigSites 域覆盖） |
| 纪律核 | 标记域 era 无关裁定（28 号 v1.1：遗迹本体=上一代建筑、标记=新一代地图 UI——不属单代美术包）；跨层契约=RelicMarkerBoard 冻结常量↔stem 逐一对应（RelicArtMappingTests 实锚） |

## 四、T-126 四人格全量+语音（commit 2a59e72，2026-10-17）

| 项 | 内容 |
|---|---|
| 任务卡 | 08 行：4×≤50 模板+能力等价测试+4×3 组语音（F-109/F-137 [C-13,C-25]，G10 冒烟+等价，L） |
| 交付物 | `Narrative/PersonaLineBank.cs`（4×50=200 行台词库，16 面覆盖矩阵）+ `Audio/PersonaVoice.cs` 扩展（音色=时代×人格 16 行变体）+ `Presentation/PersonaSwitch.cs`（三联动切换决策：立绘+音色+台词同拍一致）+ `content/narrative/persona-lines.json`（209 行）+ `content/audio/persona-timbres.json` 扩展（+195 行）+ presentation 接线（AudioDirector/NarrativeHud/Main.cs PERSONA 段/InfoLayersUi） |
| 测试 | PersonaLineBankTests（新 364 行：台词库容量/面覆盖/契约）+ PersonaVoiceTests（扩展：16 音色变体）+ PersonaSwitchTests（新：三联动等价）——14 文件 1753 行 |
| G10 冒烟判据 | `PERSONA_OK lines=200 tones=16 switch=linked bank=4x50 faces=16 eras=4 voice_era=era1 cues=6 portrait=loaded restored=advisor`（Main.cs 5l 段，headless 实锚——本回填时点冒烟再捕获同形） |
| 判定 | **过**（回填口径）——commit 仅一行 message、无独立报告文档；**本表按 diff 清单+PERSONA_OK 冒烟判据回填**。落库时点门禁=v10 gate 2026-10-17 run_all 全绿（同上） |
| 纪律核 | 与 T-127 并行纪律执行（43 号头部「并行纪律」行：人格闲聊行归 T-126，story-e4 波间池只落 advisor 骨架）；能力等价=四人格共享同一能力面（PersonaSwitchTests 等价断言，G10 双轴之等价半边） |

---

## 五、批次总账与回填声明

- 四任务 5 commits，时间序 e4324f8 T-123（10-16 16:51）→ 73f610d T-125（10-17 01:46）→ 3e51ba4+38fd5c7 T-124（02:07/02:08）→ 2a59e72 T-126（09:17）；同窗其后=d7095b8 T-127（07:49）/f0b4eb0 T-128（09:50）/da2078d T-130 v1.0 gate（11:09），不在本表。测试基线 5924（W6 出口）→ 6012（T-123）→ 6094（T-124）→ … → 6157（v10 gate 实测）。
- **回填局限（诚实声明）**：T-123/T-124 的门禁数字引自 commit 正文（当时实测）；T-125/T-126 落库时无逐任务 REV/报告与门禁数字——其「过」的判定依据=任务卡验收面在测试树的现状断言+其后同日 v10 gate 全绿记录+本回填 commit 的 run_all 全绿（见下），非历史时点重放。
- 本回填所在 commit（W7 收口修复 F1~F3）完成门槛：`bash tools/ci/run_all.sh` 全绿（性能门禁安静窗口）——实测结果登记于本 commit message 与 08 v1.15 变更记录。
