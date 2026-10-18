# REV-20261018-T141 — 移动端移植评估与适配（触控原型+可行性报告）

| 项 | 内容 |
|---|---|
| 任务卡 | T-141（移动端移植评估与适配；Wave 8 批 6，D/F 泳道，组件 C-21/C-31；前置 T-106；08 行验收词原文="触控原型+可行性报告（不承诺排期）"） |
| 验收（08 行原文） | 触控原型+可行性报告（不承诺排期） |
| 状态 | **全量交付**（评估性质：可行性报告+触控原型面+分辨率缩放档开关；**非移植本体**，零排期承诺） |
| 结论 | **有条件可行（Android 优先）**——引擎面不构成 D-34 同源阻断；七项条件/工作面见 49 号文 §6 |
| 提交 | 单 commit（报告+原型面+REV，见 §5） |

---

## 1. 交付物清单（全部可测、已测）

1. **`docs/engineering/49-t141-mobile-feasibility.md`**（新，报告主体）：五面评估（引擎/性能/输入/UI/分发）+七条条件+来源日期汇总（外部条目全部标注初检+检索日期 2026-10-13；查不到的两项明写查不到）。
2. **`game/src/EraCore/Presentation/TouchGestureMapper.cs`**（新，纯逻辑零引擎类型）：触控事实→决策流（BuildPress/BuildDrag/BuildCommit/BuildCancel/TwoFingerPanZoom）；防误触核心性质=双指手势期零建造决策；乱序/重复事件惰性化（永不异常、永不幻影提交）；双指事实（span/center 增量）随决策携带。
3. **`game/src/EraCore/Presentation/MobileViewportPolicy.cs`**（新，纯逻辑）：viewport 缩放档开关——桌面整数档逐字节不变（委托 ViewportScale.Fit）；触屏分数档（0.05 步进网格、仅严格优于整数档时启用、≥1 钳制、偏移恒非负）。
4. **`game/presentation/EraTD/scripts/BuildInteractionUi.cs`**（改，+192/−3）：
   - `_GuiInput` 触控分支：`InputEventScreenTouch/Drag → TouchGestureMapper 决策 → ApplyTouchDecision`；
   - `ApplyTouchDecision`：决策→**既有鼠标面同一** BeginDrag/UpdateDrag/CommitDrag/Cancel（只加输入映射，不改命令语义——CommitDrag 仍走暂停分支/PlayerBuildLane→ActionRegistry→SimCommand 同一咽喉）；
   - `ApplyMobileViewportTier`：MobileViewportPolicy 决策→运行时切 `Window.ContentScaleStretch=Fractional`（4.7.2 GodotSharp 反射实证该属性存在且枚举 Fractional/Integer；headless 短路零副作用；整数档=project.godot 原样）；
   - `CellAtWorld` 静态换算（鼠标 CellAtMouse 改为委托——两输入同一 floor(world/cell)）；
   - BUILD_TOUCH headless 冒烟第三幕（见 §2）。
5. **`game/src/EraCore.Tests/Presentation/TouchGestureMapperTests.cs`**（新，6 用例）+ **`MobileViewportPolicyTests.cs`**（新，10 用例）：合成事件→决策→**真命令流端到端**（PlayerBuildLane.Build→Ok+塔落子+K-8 玩家事件落流）+ 触控/鼠标同事件流同格集同命令（语义保持证据）。

## 2. 引擎面证据链核心（49 号文 §1 摘要）

- **D-34 Web 阻断不同源**：Web=模板缺失+编辑器验证层硬拒（REV-T106 §2 三重实证）；移动=**本仓 mono 模板包实测含** `android_debug.apk`/`android_release.apk`/`ios.zip` 三件 + 编辑器符号 `EditorExportPlatformAndroid`×9/`EditorExportPlatformIOS`×4 编译在 + 无移动版 not-supported 硬拒文案（二进制全文仅命中 Web 拒绝原文）。
- 官方口径（初检 2026-10-13）：Android/iOS C# 导出=实验性（自 4.2）；iOS 仅 macOS 宿主可导出。
- 社区风险在案（初检 2026-10-13）：#99021/#103915 启动崩溃先例、#97775 Android 无 NativeAOT/JIT 启动慢——实验级接受+真机烟测准入列条件 #1。

## 3. 冒烟与门禁证据（全部真实运行）

- **BUILD_TOUCH**（smoke 第三幕，合成事件**经 _GuiInput 事件分发入口**驱动）：
  `tap=build tap_built=True tap_journaled=True drag=rect drag_built=2 dragging=True twofinger=cancel+panzoom cancelled_idle=True gesture_zero_build=True recovered=True decisions presses=4 commits=2 cancels=1 panzooms=1 towers=25 player_journal_events=19 tick=3269`
  ——八项自检任一不过即 Quit(1)；既有 BUILD_OK/BUILD_V2 两幕逐字段不变（towers_placed=7/parked=2 flushed=2 等——确定性零漂移）。
- **dotnet test（新测试）**：TouchGestureMapper+MobileViewportPolicy 16/16 绿（首跑 2 红=第三指误返 BuildCancel+journal FileShare 争用—— mapper 修正为手势期新指惰性/读改 JsonlJournalWriter.ReadAll，复跑全绿；过程如实记录）。
- **run_all（提交前，六步）**：`G2 红线 9 检查 0 FAIL 0 WARN｜G2.5 资产 641 文件 0 拒绝｜G2.6 模组 3 装载 0 失败｜G1 6700 passed/0 failed（1198s）｜G3 Release 零警告（20s）｜G4 SMOKE_OK（28s）——总体 PASS`。诚实标注：①G1 时长 1198s 显著高于批 5 的 ~239s 档——并行 T-139 泳道同时在途（SimCommandQueue.cs 改动+EraNet/ 新增，见 §4），机器负载高，但全绿且性能门禁（含 Spike1/SimGrouping）在其内通过；②本次 run_all 编译面含 T-139 未提交改动（其 Enqueued 观察者接缝，单机路径字节不变的声明口径属其泳道）——本泳道 6 文件不含其接触面。

## 4. 并行泳道现场

T-139（联机实现）同刻在途：`game/src/EraNet/`（未入 sln）+`SimCommandQueue.cs`（Enqueued 事件接缝）。本泳道 `git add` 仅 7 路径（§5），未触碰其文件；BuildInteractionUi 在批 6 冲突面声明=Main.cs（本泳道未改 Main.cs，触控开关落位 BuildInteractionUi._Ready 规避）。

## 5. 提交与复现

- commit：`T-141: mobile feasibility report + touch input prototype (mapper/policy/BUILD_TOUCH) + viewport scale tier`（单 commit；仅本泳道 7 路径：报告+REV+EraCore 两纯逻辑件+两测试件+BuildInteractionUi）。
- 复现：`bash tools/ci/smoke_godot.sh`（看 BUILD_TOUCH 三真值段）｜`dotnet test game/src/EraCore.Tests --filter "FullyQualifiedName~TouchGestureMapper|FullyQualifiedName~MobileViewportPolicy"`（16 用例）｜`python tools/ci/redline_scan.py`（9 检查）｜报告复现：`python -c "import zipfile; ..."` 枚举 tpz（49 号文 §1.2 表）。

## 6. 局限与诚实声明

- **无移动真机**：性能面为量级推算（49 号文 §2.2 明示"推算非实测"）；Tier B 真机 60s 实测属条件 #2。
- **未做导出实操**：本仓环境无 Android SDK/JDK 17/macOS（09 环境事实）——引擎面结论靠模板包枚举+二进制符号+官方文档三方交叉，未实际产 APK/IPA（评估性质内合法，声明之）。
- **原型边界**：长按轮盘/屏上暂停变速按钮/双指相机接线/移动 UI 放大档/流场移动配额 2048 落值——全部为报告建议（§6 条件 #3~#5），不在本原型面。
- 外部事实全部初检（单次检索 2026-10-13，待复核）；本仓事实全部可复现（命令在 §5）。
