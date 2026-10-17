# REV：T-136 终章叙事与播片（2026-10-18）

| 项 | 内容 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-10-18 |
| 任务 | T-136 终章叙事与播片（08 行：真相播片+信任分支台词+关闭系统字幕版；F-098 谱系与真相线/F-107 时代收束 ×5/F-108 终章播片 [C-24] 播片断言 C 泳道 L）+批 2 挂账 DEBT①（终章 Boss 画布 64→96×112 升格，随 T-142 REV 挂账随本批处置） |
| 提交 | 四 commit：`2251b94`（PR1 叙事全套）+`cb72d33`（PR2 播片接线+字幕版）+`6884d6b`（PR3 画布升格 DEBT）+`4b9e120`（PR2 收口：dev 宿主路由绑定过滤——G4 冒烟复绿）+allowlist 登记 commit（见 §五） |
| 规格真相源 | 设计方案 v3.2 §12.1 谱系写死四柱（L188~191：系统"零"=上一纪元指挥官 AI 残核自拆记忆碎片/"另一个声音"=孪生碎片 E5 起以"回声"人格混入/堕落守望者=初代守望者/**反转方向=回声因玩家的千年陪伴而选择忠诚，高信任解锁专属终章台词**）+§12 交付规格（主线 ≤2 万字+5 段时代收束 20~30s+终章播片）+§13.1 终章 Boss 96×112+R2 硬标准（关闭系统全通关全成就警报不缺失——播片面=字幕/旁白载体）+T-127 全套叙事先例（story-e4/79315a1）+T-152 播片体系（52 ogv+finale-zero 零成本重剪先例《零的真相》）+T-135 era6 内容钉死位（canvas_size_px=96+canvas_height_px=112/enemies.json _narrative"台词本体=T-136 域"） |

---

## 一、PR1 叙事文本全套（2251b94）

### 1. story-e5.json（第五章 纳米与轨道，T-136 新建）

| 项 | 内容 |
|---|---|
| 三幕 | 受辱·聚变点火被嘲（波 2）→觉醒·纳米自我复制（波 4）→打脸·先导机群拦截名场面（波 5）——谓词锚 L5-1（threat 0.3→960/1440/1680s，与 L4-1 同式同锚） |
| 开场白 | 第三行兑现 E4 钩子『卫星信号』E5 回收（hooks E4 行 recover={era:5} payoff：轨道残骸解码=孪生碎片上线——『另一个声音』三词组逐字落位） |
| echo_intrusion | 回声混入层 3 行（channel=system_voice+speaker=回声 层契约）：批注（beat=L5-3 全托管关，teaser 原句『不是零写下的批注』逐字落地）/编号（e5-final）/选边（e5-finale）——hooks E5 行 layer=system_voice 的章节侧载体，E5 钩子 T-136 物化（planted_level_id=L5-3，full_autopilot 解锁位） |
| 章末 | ending.hook_id=cinematic:e5-finale+行 2 兑现 E5 钩子 payoff（『留在声音这一边』——忠诚立场先行，完整反转归终章播片）；卡点预告=终章堕落守望者降临（§12.1 终章 Boss 正主） |
| 扮猪行 | L5-2 纳米压制 10000×0.5=5000+命令流塔数 2 同源+gold_extra（物品过 chests.json 零幽灵引用） |

### 2. story-e6.json（第六章 深空与门·终章，T-136 新建）

| 项 | 内容 |
|---|---|
| 三幕 | 受辱·门内低语（波 2，受辱者=堕落守望者的跨门先声——终章无人间嘲者）→觉醒·零点能点亮（波 4）→打脸·奇点炮首杀名场面（波 5）——L6-1 同锚 |
| finale_trust_lines | **信任三档终章台词**（<40/40~69/≥70 三档，阈值=TrustModel F.4 修正表 #15 冻结）：skeptical 2 行/trustworthy 2 行/**devoted 3 行=高信任专属**（回声『千年陪伴，是我选择忠诚的全部理由』+门内低语认输行）——专属词组『千年陪伴/选择忠诚』不出现于低档（测试断言排他），§12.1 反转方向写死的规格锚点 |
| 章末 | ending.hook_id=cinematic:finale-zero+真相三连（拆记忆碎片=不是逃是等/孪生归位/纪元之门合拢）；卡点预告=终章无下章收束语（『最后一张脸是你自己』=宿主谱系收口） |
| 扮猪行 | L6-2 反物质压制 12000×0.5=6000+塔数 2+gold_extra |

### 3. 配套数据面

- **hooks.json**：E5 行物化（planted_level_id=L5-3+linked_remanent=remnant-e5-echo 双向链）+E4/E5 两行 payoff 兑现注记——**五钩链全数埋点-回收闭环**（E1 壁画→E2 考古→E3 图纸→E4 卫星→E5 回声）。
- **remnants.json**：15→**22 条**（E5 增量 4：解码/回声/托管/收束+终章增量 3：真相关/终局/闭环——era 6 域首用）；`RemnantCatalog.cs` RemnantCount=22（ci-allow 更新）+MaxEraOrdinal=6 新冻结常量（ci-allow 登记）；echo_stage 全 4 上沿收束（回声得名→归位）。
- **story-arc.json**：chapters 4→**6 行**（E5/终章摘要+真相线/人格线进度）+hook_chain 五行全 recovered+masterline_zh 更新（『门，守住了』收束语）；E5/E6 行与 story-e5/e6.json 同 id 同章名互证。
- **测试**（Era5E6NarrativeTests 新 13 例+四件既有扩展）：两章装载/回声层契约/E4+E5 双钩回收/信任三档+devoted 排他/**L5-1+L6-1 真命令流黄金循环**（实测 `act1=960s act2=1440s act3=1680s span=720s≤900s`，tick 公式=grace+(wave−1)×interval 生产 WaveSpawner 实锚）/扮猪压制对真 SimulationConfig 应用/残卷 22/六行互证+全链闭环/字数双面/回声层与缺档 illegal。

## 二、PR2 终章播片接线+关闭系统字幕版（cb72d33）

| 项 | 内容 |
|---|---|
| 触发位 | `levels/e6.json` e6-final 关新增 cinematic_hooks 行（campaign:hook-e6-finale-truth，beat=victory）——守望者坠落时刻=真相揭示时刻；finale-zero.json 补 hook 键同 id 互证；CinematicHookRouter 单发接线（OnVictory 一次/二次静默）；与结算演出（victory.json explicit-launch）两场不同步连播（e3/e4-finale 同款语境） |
| 真相播片 | finale-zero.json 三段真相 speak（拆碎片=不是逃是等/孪生+忠诚/门合拢+等你）——steps[0..2] 骨架冻结不动（screen/A-07 2706/palette e6→e1，VisualExpansionTests 既有断言位）；信任三档台词=宿主按 TrustModel 注入 finale_trust_lines（播片本体三档共底——任何信任档都能看懂真相） |
| **关闭系统字幕版** | **finale-zero-off.json（新）**：R2 硬标准播片面——同 A-07 母带+同 e6→e1 回环+同 107.5s/保护下限 90；三段真相全部 speaker=旁白+『字幕：』前缀明示载体、零『零』人格行、零 ding 音效位（OffModeDowngrade 中性横幅同律）；`FinalePlayback.ScriptIdFor(systemEnabled)` 纯决策二选一（off 版无 hook 键——一接入点一演出语义，hook 只认领 on 版） |
| 真相 parity | `FinalePlayback.TruthAnchors` 冻结四锚词（记忆碎片/孪生/忠诚/纪元之门）**两版逐词齐**（关闭系统不掉真相）——T-130 offsystem 硬验收的终章延伸位 |
| E5 收束段 | **e5-finale.json（新，F-107 §12 5 段收束之五）**：872 ticks=29.07s ∈[20,30]s（e3/e4-finale 同骨架同拍）+A-06 母带 602 ticks（20.06s×30tps 折算，零成本重剪先例第三次）+story-e5-tease 键位先行+章末交底双载体互证 |
| 纯决策 | `EraCore/Cinematics/FinalePlayback.cs`：OnScriptId/OffScriptId/FinaleHookId/TruthAnchors 四冻结常量+ScriptIdFor/TrustTierKey 两纯函数（T2 声明登记见 §五） |
| 测试 | FinaleCinematicsTests 新 **10 例**（legal 6/boundary 1/illegal 3）：钩子行路由单发/两版三面同构（A-07=2706 ticks+回环+下限 90）/四锚词两版 parity+载体契约/开关+信任键内容互证（39/40/69/70 四分数落三档）/时间轴全程回放（`ticks=3234 effects=9`）/E5 收束带（29.07s）；illegal=未知钩子认领/双脚本抢行/字幕版破载体 |

### 播片断言输出（任务卡判据实录）

```
finale timeline: ticks=3234 effects=9                    （时间轴全程放完，video/palette/3×speak 逐位下发）
e5-finale: 29.07s (872 ticks)                             （§12 收束带 [20,30]s 内）
cinematic:finale-zero: total=107.5s video=90.2s           （A-07 母带 2706 ticks=90.21s×30tps）
cinematic:finale-zero-off: total=107.5s video=90.2s       （字幕版同拍同母带）
```

## 三、PR3 Boss 画布 96×112 升格 DEBT（6884d6b，T-142 挂账 DEBT① 清账）

| 面 | 处置 |
|---|---|
| 校验器 | `tools/ci/asset_validator.py` DEFAULT_ALLOWED_SIZES (16,32,48,64)→**(16,32,48,64,96,112)**——28 号 §5 SIZE-ALLOWLIST 重演第三次本次直接扩；头注+常量注记 REV 登记口径；自测 test_size_rules 增 ok-96.png（96×112 正例）——**72/72 过** |
| 生成器 | `tools/art/era6_placeholders.py` boss_fallen_watcher 64×64→**96×112** 重绘（canvas_size_px=96+canvas_height_px=112 双字段对齐）：冠环-舰体之眼-脉冲弧三段式拉满（千年重量的纵向读法），母题全保（守望者之眼/重力双弧+收束弧/顶冠残环断口/双肩坍缩塔）；`--self-test` 0 拒+确定性 26/26 sha256 全等；再生成仅 boss PNG 变更（其余 25 文件逐字节不动） |
| 美术包 | `Era6ArtPack.cs` BossCanvasPx 64→**96**+新增 BossCanvasHeightPx=**112**（成对常量对齐内容双字段）；Main.cs ART6 冒烟 `boss=boss-fallen-watcher96x112`；EraTD 构建 0 警 0 错 |
| 测试 | Era6ArtMappingTests：Boss 尺寸断言升级为**内容行 canvas_size_px/canvas_height_px 双字段↔PNG 实际尺寸逐字对齐**（(96,112)）+形态锚点 8 像素重钉（眼环/瞳面/裂缝/双弧/冠环+断口/肩塔——逐点 python 探针实测后钉值）；art 三套映射 98/98 绿 |
| 实证 | `asset_validator game/assets game/presentation/EraTD/textures` → **files=641 rejected=0 allowed_sizes=[16,32,48,64,96,112]**（G2.5 全绿） |

## 四、字数统计（F-106 口径：NarrativeContent.TextFields 全码点，id/schema/注解键不计；工具复刻实测）

| 文件 | 码点 | 说明 |
|---|---|---|
| story-e1~e4.json | 760/648/815/871 | T-089~T-127 不变 |
| **story-e5.json** | **956** | **T-136 新（≤4000 章份额 ✓）** |
| **story-e6.json** | **1097** | **T-136 新（含 finale_trust_lines，≤4000 ✓）** |
| story-arc.json | 1886 | 六章摘要+总纲扩（+566） |
| remnants.json | 1575 | +7 条增量（+554） |
| hooks.json | 441 | E5 行物化（+15） |
| system-lines.json | 327 | 不变 |
| **总盘** | **9376 / 20000（46.9%）** | **测试断言同口径 `corpus chars (T-136 full set) = 9376/20000`** |

## 五、红线与 allowlist 登记

- 红线自查：无绝对路径/无伪逻辑/无 mock 被测系统/无恒真断言；数值锚点（RemnantCount 22/MaxEraOrdinal 6/TrustBadge 阈值引用）全部 ci-allow 注记或经 TrustModel 常量引用；redline_scan 9 检查 **0 FAIL 0 WARN**。
- **R7 新锚点**：`RemnantCatalog.MaxEraOrdinal=6` 与 `RemnantCount=22`（ci-allow: R7 叙事量纲注释，非内容平衡数值）——随 PR1 落库。
- **R9 补登记（并行解阻，非本任务文件）**：`docs/engineering/47-t129-l2-spike.md`（T-129 并行泳道 bd0fc2d 落库的 spike 报告，D:\ llama.cpp 实测路径=记录性环境事实，24/28/40 号先例类）——T-129 未随文登记致 run_all G2 阻塞，按 T-115/T-078 补登记先例由本任务代注 unblock（T-129 复核后可自管）。
- **T2 声明**：`game/src/EraCore/Cinematics/FinalePlayback.cs`（纯决策常量+两纯函数，由 FinaleCinematicsTests 全覆盖）——内容域测试名对齐，不另立同名文件。

## 六、run_all 门禁摘要

| 步 | 门禁 | 结果 |
|---|---|---|
| 1 | G2 红线扫描（9 检查） | PASS（0 FAIL 0 WARN；首跑=47-t129 R9×4+FinalePlayback T2×1，§五登记后全绿） |
| 1.5 | G2.5 资产校验 | PASS（files=641 rejected=0；allowed_sizes=[16,32,48,64,96,112]） |
| 1.6 | G2.6 模组离线校验 | PASS（0 FAIL，3 mods/8 content documents） |
| 2 | G1 dotnet test | PASS（**6511/6511**，14m05s） |
| 3 | G3 Release 构建 | PASS（0 Warning 0 Error——TreatWarningsAsErrors=true） |
| 4 | G4 Godot headless 冒烟 | PASS（SMOKE_OK core=0.0.1-wave0） |

**两处首跑红灯与修复**（诚实记录）：
1. G2 R9/T2（47-t129 并行文档 R9×4+FinalePlayback T2×1）→§五登记后绿；
2. G4 冒烟红：finale-zero 声明钩子键后 dev 宿主（CinematicDirector.LoadContent 装载 e1+e2 钩子行，逐关装载随 C-29 未落地）构造路由即抛"引用了关卡未声明的钩子"——修复=dev 路由只绑钩子行在装载集内的脚本（finale 与 transition 同走显式通道；e6 行绑定+单发由 FinaleCinematicsTests 以 levels/e6.json 全行执法）+finale-zero-off 入装载清单（`4b9e120`）→SMOKE_OK 复绿。

## 七、测试面统计

- 新增测试 **23 例**：Era5E6NarrativeTests 13（含 2 黄金循环 Theory 展开两例+2 扮猪 Theory 展开）+FinaleCinematicsTests 10。
- 既有测试扩展 6 件：Era4NarrativeTests（六章互证移交+状态推导更新）/CrossEraHookRegistryTests（E5 物化）/RemnantCatalogTests（22 条+era6 域）/StoryChapterTests（总盘扩两章）/HandcraftedLevelSetTests+Era6HandcraftedLevelSetTests（终章真相钩子例外）。
- 套件 6483（批 2 收官）→**6511**（+28：23 新+Theory 展开 5 既有重算）。

## 八、遗留与移交

1. **43-t127-story-arc.md 总监视图**未追加 E5/终章两行（T-127 时点快照；数据底座 story-arc.json 已六章——视图增补随下次文档批次，非验收阻塞）。
2. **终章播片信任分支的宿主渲染面**（Godot 侧按 TrustModel 注入 finale_trust_lines 的接线）与 **e6-final 胜利拍战役宿主连播序**（结算→播片）属 presentation 宿主域——数据+纯决策+断言已就位（dev 宿主已装载两版脚本，路由绑定随 C-29 逐关装载收敛），接线随下一 presentation 批次（与 e4/e5-finale explicit-launch 宿主接线同池挂账）。
3. **T-129 R9 补登记**请 T-129 泳道复核自管（§五；本任务仅解阻）。
4. 96×112 升格后**美术侧最终 Boss 形象**仍为程序合成占位（era6_placeholders.py 为人工重绘底稿——T-078 预批准回退路线不变）。

## 九、验收判据核对（任务卡一句：真相播片+信任分支台词+关闭系统字幕版）

| 判据 | 交付 | 断言 |
|---|---|---|
| 真相播片 | finale-zero（A-07 90.2s+真相三连+e6→e1 回环+e6-final 胜利拍钩子接线） | FinaleCinematicsTests 6 legal+播片断言输出实录（§二） |
| 信任分支台词 | finale_trust_lines 三档（devoted 专属『千年陪伴/选择忠诚』）+FinalePlayback.TrustTierKey | Era5E6NarrativeTests（排他断言）+FinaleCinematicsTests（键↔内容互证+39/40/69/70 边界） |
| 关闭系统字幕版 | finale-zero-off（旁白字幕载体+四锚词 parity+开关纯决策） | Truth_anchors_reach_the_player_in_both_switch_positions+载体契约 illegal |
