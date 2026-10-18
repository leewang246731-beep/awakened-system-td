# T-141 移动端移植评估与适配 —— 可行性报告 + 触控原型面

- **任务编号**: T-141（移动端移植评估与适配；08 任务表 W8 批 6，D/F 泳道，组件 C-21/C-31；前置 T-106）
- **执行日期**: 2026-10-18（外部检索事实以各条目标注的「检索日期」为准）
- **版本**: v1.0（首版）
- **状态**: 已完成（评估性质——交付=本报告+触控原型面，**不是移植本体**）
- **验收口径**: 08 任务表 T-141 行原文——"触控原型+可行性报告（**不承诺排期**）"
- **结论速览**: **有条件可行**（条件见 §6；引擎面不构成 D-34 同源阻断，性能量级可行带宽，输入/UI/分发各有明确工作面）

---

## 0. 一句话结论

**移动移植"有条件可行"**：Godot 4.7.2 mono 的 C#→Android/iOS 导出是"实验性支持"而非 Web 那样的引擎级硬拒（本仓 mono 模板包实测含 android/ios 模板三件，编辑器导出平台编译在——与 REV-T106 §2 的 Web 三重阻断证据链形成对照）；桌面基准（低压 U 系 CPU）逻辑帧 1.336ms 中位对 10ms 桌面线有 7.5× 裕量，移动 SoC 大核同量级推算仍全带内；真正的工作量在输入映射（已落原型）、移动 UI 档（字号/点击区低于 48dp/11pt 建议线）、流场配额移动档（建议 2048 起）与分发工具链（iOS 需 macOS 宿主+年费，Windows 环境无 Mac=硬前置）。**本报告不承诺排期**（08 验收词原文）。

---

## 1. 引擎面：C#/.NET → Android/iOS 导出支持现状

### 1.1 官方文档口径（检索日期 2026-10-13）

| 平台 | 支持等级 | 官方原文（意译） | 来源 |
|---|---|---|---|
| Android | **实验性**（自 4.2） | "Projects written in C# can be exported to Android as of Godot 4.2, but support is **experimental** and some limitations apply." | docs.godotengine.org/en/stable `tutorials/export/exporting_for_android.html`（检索 2026-10-13） |
| iOS | **实验性**（自 4.2，有附加限制） | "iOS support is currently **experimental** and has a few limitations."；"Exporting to iOS can only be done from a **macOS device**." | docs.godotengine.org/en/stable `tutorials/scripting/c_sharp/index.html`（检索 2026-10-13） |
| Web（对照） | **不支持** | "cannot be exported to the web platform"（建议 Godot 3） | 同上 |

平台总表原文："Since Godot 4.2, projects written in C# support all desktop platforms (Windows, Linux, and macOS), as well as Android and iOS."（检索 2026-10-13）。Android 侧通用要求：OpenJDK 17+Android SDK（Platform-Tools 35.0.0+/Build-Tools 35.0.1/Platform 35，官方文档，检索 2026-10-13）。

### 1.2 本仓实证（钉死的 4.7.2 mono 工具链，2026-10-18 实测）

| 事实 | 证据 | 与 Web 阻断的对照 |
|---|---|---|
| mono 导出模板包**含**移动模板三件 | `tools/Godot_v4.7.2-stable_mono_export_templates.tpz` 27 条目枚举：`templates/android_debug.apk`、`templates/android_release.apk`、`templates/ios.zip` | Web=**零** `web*.zip`（REV-T106 §2.1） |
| 编辑器移动导出平台编译在 | 编辑器二进制符号扫描：`EditorExportPlatformAndroid` ×9、`EditorExportPlatformIOS` ×4 | Web 平台类在但验证器硬拒 C#（REV-T106 §2.2） |
| 无移动版"not supported"硬拒文案 | 二进制全文检索仅命中 Web 拒绝原文（"Exporting to Web is currently not supported in Godot 4 when using C#/.NET…"），无 Android/iOS 同款 | Web 有同款（REV-T106 §2.2） |
| 运行时缩放/触控 API 齐备 | GodotSharp.dll 反射实证：`Window.ContentScaleStretch(Fractional/Integer)`、`InputEventScreenTouch/Drag`、`DisplayServer.IsTouchscreenAvailable()` | —（原型面依赖，见 §3/§4） |

**判定：D-34 的 Web 阻断不构成移动同源阻断**。Web 是"模板缺失+编辑器验证层硬拒"双杀；移动是"模板在+平台在+官方标注实验性"——门是开的，等级是实验性。

### 1.3 社区实况（风险面，检索日期 2026-10-13）

- godotengine/godot#99021：C# Android 首次导出/安装即崩溃（4.3-mono/4.4 复现）；
- godotengine/godot#103915：4.4 C# Android 导出在 Quest 2/3 偶发启动崩溃；
- godotengine/godot#97775 + godot-proposals#12013：Android 无 NativeAOT，.NET 运行时走 JIT——**启动慢**是首要社区抱怨（游戏运行中性能影响报告少）；
- Reddit「State of Godot 4.4 Android/iOS using C#? Commercially」：有人商用出货，但相当比例开发者移动端保守回退 GDScript。
- 追踪主单：godotengine/godot#68153（Add C# support for Android in Godot 4.x）。

**口径**：实验性=可用但有真实崩溃先例；iOS 无 JIT=全 AOT（Godot iOS C# 模板走 AOT），AOT 对纯逻辑计算负载的影响通常为个位数百分比到 ~2 成（量级常识口径，**本项目无实测**——诚实标注：查不到针对本工作负载的公开 AOT 开销基准）。

---

## 2. 性能面：桌面基准 vs 移动 SoC 量级估算

### 2.1 桌面基准（既有门禁数字，来源=spike1-report.md §1/§3 与 REV-20261017-T153）

| 指标 | 门禁线 | 实测（中位） | 裕量 | 测量机 |
|---|---|---|---|---|
| 均逻辑帧（300 敌+1000 弹迷宫复合） | ≤10 ms（16.6ms×60% 渲染余量口径） | **1.336 ms** | **7.5×** | Intel i5-10210U（4C/8T 低压 U 系，2019）——47 号文 §1.1 环境表 |
| sim tick p99（G8 Tier A） | ≤12 ms | 1.677 ms | 7.2× | 同上 |
| 流场单切片最大（100 次全图重建） | ≤10 ms | 8.08 ms | 预算机制内 | 同上 |
| 流场单最重 tick（配额 6144） | ≤10 ms 包络 | 3.79 ms（≈0.60µs/记账格） | ~2.6× | 同上（T-153 校准） |

关键口径：**桌面门禁机本身是低压 U 系笔记本 CPU**，非桌面旗舰——这直接压缩了"桌面→移动"的性能落差假设。

### 2.2 移动 SoC 量级估算（推算非实测——无移动真机，全部为量级带）

单核算力对齐（Geekbench 6 单核，公开量级，检索 2026-10-13）：i5-10210U ≈1100~1200；中端 SoC 大核（SD 7 系/Dimensity 7 系，2022+）≈1000~1300（**同量级**）；旗舰大核（SD 8 Gen 2/3）≈2000+；小核 ≈400~700（0.3~0.6×）。

| 热路径 | 桌面实测 | 移动大核推算（×0.8~1.5） | 移动小核推算（×2.5~4.5） | 对线判定 |
|---|---|---|---|---|
| 均逻辑帧 1.336 ms | — | ~1.1~2.0 ms | ~3.3~6.0 ms | **≤10 ms 桌面线全带内**（小核最差仍有 1.7× 裕量） |
| sim tick p99 1.677 ms | — | ~1.3~2.5 ms | ~4.2~7.5 ms | ≤12 ms 带内 |
| 流场最重 tick 3.79 ms（6144 配额） | — | ~3.0~5.7 ms | ~9.5~17 ms | **小核超 10 ms 包络**——配额需降档（§2.3） |

30fps 预算口径（RenderTier L 档目标 33.4 ms，T-102 能力矩阵）下所有行裕量再翻倍。

**判定：模拟热路径在移动的可行性带=宽**（大核全绿；小核唯一压力点是流场配额，且配额是可调旋钮）。

### 2.3 流场配额移动档建议（6144 → 移动档 2048 起）

- 依据一（既有独立校准）：Web 帧预算 2048 记账格已按"悲观 4× 单位成本"独立校准——0.32µs×4×2048≈**2.6 ms/帧**，8 ms 设计切片内 3× 裕量（FlowSolveBackend 常量注释，T-153 交付物）。移动 SoC 相对门禁机的单位成本恰在 2~4× 带——2048 与移动档数学上同构。
- 依据二（确定性安全）：T-153 归因实验 [C] 五口径（墙钟切片/配额 6144/裸配额/单巨 tick/1ms 切片）**哈希全等**——配额只改吞吐不改结果（同 seed 命令流确定性底线不受移动档影响）。
- 建议：移动档 `DefaultCellsPerTick` 回落 **2048**（旗舰大核可试 3072）；首次全图重建耗时拉长（E1 128×128≈46k 记账格 ⇒ 2048 配额 ~8 tick 完成初建，桌面 6144 ~3 tick——量级差异，游戏性上首建延迟 +数百 ms 级）。
- **未做**：本任务不改生产配额（6144 是桌面/CI 档，T-153 刚校准完）；移动档值属正式移植面的真机 Tier B 实测后再钉。

### 2.4 渲染面（既有适配，零新增工作）

`project.godot` 已钉 `renderer/rendering_method.mobile="gl_compatibility"`（与桌面同后端，T-102 交付物）；渲染三档 L（33.4ms/30fps、静态烘焙关、特效 0.5）即"集显/Web 稳定档"，移动天然映射 L 档起步——`RenderTierSelector` 的 Auto 档在真机走 60 帧探测自适应（T-102 机制，无需移动特判）。

---

## 3. 输入面：鼠标精确建造 vs 触控（盘点+映射方案+原型）

### 3.1 现有交互面盘点（BuildInteractionUi 全量）

| 面 | 现有输入 | 触控映射方案 | 状态 |
|---|---|---|---|
| 格选锚定 | 左键按下（armed 后 BeginDrag） | **单指点击=格选**（单格=1×1 矩形，与鼠标单击同一交互语义） | **原型已落地** |
| 矩形扩选 | 左键拖动（UpdateDrag） | **单指拖动=矩形** | **原型已落地** |
| 提交 | 左键松开（CommitDrag→PlayerBuildLane 同一咽喉） | **单指抬起=提交** | **原型已落地** |
| 取消 | ESC | 双指手势接管即取消（防误触）+屏上取消按钮（正式面） | 原型落地前者 |
| 缩放/平移 | 无（相机固定） | **双指=缩放/平移**（span/center 增量决策流已产出；相机接线属正式移植面） | **决策流已落地** |
| 拆除 | 右键点击塔→confirm 弹层 | **长按=轮盘先例**（SystemPanel F-116 快捷轮盘=四高频军令轮盘，长按弹塔操作轮盘：拆除/查看/升级）——提案 | 报告建议 |
| 暂停/变速 | 空格/1/2/3 | 屏上按钮（暂停/1x~3x，复用 PauseClock 命令面）——提案 | 报告建议 |
| 整行/整列 | R/C | 轮盘二级项或双击锚格——提案 | 报告建议 |
| 悬停提示 | hover tooltip（灰度原因/造价缺口） | 无 hover：长按弹卡片（同拆除轮盘）+状态栏文字已承载（SetStatus）——提案 | 报告建议 |
| 建造栏选塔 | 鼠标点击按钮 | 直接点击（按钮=天然触控面） | 引擎原生 |

### 3.2 原型落地物（本任务交付，全部可测已测）

1. **`game/src/EraCore/Presentation/TouchGestureMapper.cs`**（新，纯逻辑无引擎类型）：触控事实（指号/按下/抬起/移动/位置 px）→ 决策流（BuildPress/BuildDrag/BuildCommit/BuildCancel/TwoFingerPanZoom）。防误触核心性质：**双指手势期零建造决策**（单测断言）；乱序/重复事件（重复按下、未知抬起、未按下移动）全部惰性化。
2. **`BuildInteractionUi` 触控输入分支**：`InputEventScreenTouch/Drag → TouchGestureMapper 决策 → 既有鼠标面同一 BeginDrag/UpdateDrag/CommitDrag/Cancel`——**只加输入映射，不改命令语义**（提交仍走 PlayerBuildLane→ActionRegistry→SimCommand 同一咽喉；触控/鼠标同事件流产出同格集同命令载荷，单测证明）。
3. **BUILD_TOUCH headless 冒烟**（smoke 第三幕）：合成触控事件**经 _GuiInput 事件分发入口**驱动，断言 tap→建造命令真实落子（塔+1/K-8 玩家事件+1）、drag→矩形提交、双指→取消+手势+零命令、手势后恢复——八项自检任一不过 Quit(1)。
4. 已知摩擦（正式移植面处理）：Godot 默认 `emulate_mouse_from_touch=on` 时真机会同时收到触控+模拟鼠标事件——本原型下双路汇入同一控制器且幂等（重复 Commit 在 Idle 态返回 null 零命令），无重复建造；正式移植建议显式关该开关（本轮不动 project.godot，保持桌面行为零改动）。

---

## 4. UI 面：分辨率与控件适配

### 4.1 现状盘点

- 基准分辨率 640×360（F-125 像素渲染基线；`window/stretch=canvas_items+keep+integer`）；渲染侧 `ViewportScale.Fit` 整数缩放 floor(min(w/640,h/360))≥1。
- 竖屏手机 1080×2340：整数档=1×（640×360 居中，letterbox 占屏 ~70%）——**整数档在竖屏不可用**；横屏 2340×1080：整数档 3× 可用。
- 建造栏按钮 72×48 逻辑 px（BuildBar）；竖屏分数档 1.65× 下物理 ~119×79 px——按 420dpi（≈2.625x）折 **~45×30 dp**，低于 Android 48dp 触控目标建议线（Material 指南，检索 2026-10-13）；默认字号 16 逻辑 px→~10 dp，低于 11pt/12sp 最小可读建议（Apple HIG/Material，量级口径）。**判定：需移动 UI 放大档（~1.6×）——正式移植面，原型未做**。
- 地图 16×24 格纵深（E1）→ **横屏为推荐姿态**（塔防视野宽正面）；竖屏可玩但视野裁切，靠双指平移补偿（§3 决策流已备）。

### 4.2 分辨率自适应开关（本任务交付）

**`game/src/EraCore/Presentation/MobileViewportPolicy.cs`**（新，纯逻辑）：桌面=整数档逐字节不变（委托 ViewportScale.Fit）；触屏=分数缩放档（0.05 步进网格下取整，仅严格优于整数档时启用，≥1 钳制，居中偏移恒非负）。Godot 侧应用点=`Window.ContentScaleStretch=Fractional`（4.7.2 GodotSharp 反射实证运行时可写；headless 零副作用；整数档=project.godot 原样，桌面零改动）。竖屏 1080×2340：1.65×（1056×594 居中）替代 1× letterbox。单测覆盖桌面不变/竖屏获益/横屏不换档/网格落点/钳制/非法尺寸六面。

折衷注记（诚实声明）：分数档+nearest 滤镜会引入非整数像素不均匀（像素艺术代价）——移动优先画面覆盖率的取舍；正式移植可改选"整数档+expand 纵向裁切"或"游戏层整数+UI 层独立分数缩放"二途（原型取前者简单档，已在代码注释声明）。触屏事实门=`IsTouchscreenAvailable() 或 android/ios feature`——即桌面触屏笔记本也会进分数档（其 UX 与手机一致；非触屏桌面恒整数档，行为零改动）。

---

## 5. 分发面（免费开源游戏口径）

| 渠道 | 成本 | 摩擦 | 来源（检索 2026-10-13） |
|---|---|---|---|
| **GitHub Releases 托管 APK** | $0 | 用户侧载需"允许未知来源"；无自动更新 | 既有发布链（v1.0.0 同仓先例） |
| **itch.io**（Android 页） | $0 | itch 官方 app 仅 Win/macOS/Linux；Android 游戏=APK 直下侧载；Play Protect 拦截有社区记录；非官方客户端 Mitch 存在 | itch.io/app 实页+itch 论坛（"blocked-by-play-protect"线程） |
| **Google Play** | **$25 一次性** | 个人开发者账号 **12 测试者×14 天**闭测规则（2023-11 起）才准上生产；免费无 IAP=标准审核 | play.google.com/console/signup+Google 开发者政策页 |
| **Apple App Store** | **$99/年** | **导出宿主必须 macOS+Xcode**（官方文档）；本仓环境=纯 Windows（09 环境事实）——**工具链硬前置**；免费游戏审核仍需隐私政策等合规件 | developer.apple.com 口径（多源交叉：sched.com 指南/Passion.io 对比，均引官方价） |

**判定**：Android 侧 $0~25 可起步（APK 直发与现行 itch/GitHub 免费链兼容）；iOS 是"年费+Mac 宿主+实验性导出"三重前置——若做，建议 Android 验证后再议。

---

## 6. 结论与条件（不承诺排期）

**结论：有条件可行（Android 优先）。**

| # | 条件/工作面 | 等级 |
|---|---|---|
| 1 | 引擎面：实验性等级接受（社区崩溃先例 #99021/#103915 在案；JIT 启动慢）——需真机烟测准入 | 已评估，风险在案 |
| 2 | 性能面：量级推算可行带宽（§2.2）；**Tier B 真机 60s 实测未做**（G8 真机档，需设备） | 量级已推算，实测待设备 |
| 3 | 流场配额移动档 2048（§2.3，确定性安全已证，数值待真机钉） | 建议已给 |
| 4 | 输入面：触控映射原型已落地（§3.2）；长按轮盘/屏上暂停变速按钮/相机双指接线=正式面 | 原型已落地 |
| 5 | UI 面：移动放大档 ~1.6×（按钮 45×30dp<48dp 建议、字号 ~10dp<11pt 建议）；竖屏推荐改横屏引导 | 已盘点，正式面 |
| 6 | 分发面：Android=APK 直发/itch/Play（$25+12 测试者规则）；iOS=年费+Mac 硬前置 | 已盘点 |
| 7 | LLM 面（移动 BYOK）：L3 云端 Key 与移动网络策略、L2 本地推理（1.93GB 模型，47 号文）移动档默认关——沿 WebConstraints 三档表扩展平台列 | 正式面建议 |

**本报告为评估性质，不构成任何排期承诺**（08 任务卡验收词原文："触控原型+可行性报告（不承诺排期）"）。

---

## 7. 来源与检索日期汇总

**口径：下表全部外部条目=初检（单次检索，待复核）；本仓条目=可直接复现（路径/命令在表）。**

| 来源 | 日期 | 用途 |
|---|---|---|
| docs.godotengine.org/en/stable（exporting_for_android / exporting_for_ios / c_sharp/index） | 检索 2026-10-13 | 官方支持等级口径（§1.1） |
| godotengine.org/article/platform-state-in-csharp-for-godot-4-2（2024-01） | 检索 2026-10-13 | 4.2 起 Android/iOS 实验性支持沿革 |
| godotengine/godot #68153 / #99021 / #103915 / #97775；godot-proposals#12013 | 检索 2026-10-13 | 社区崩溃/启动慢先例（§1.3） |
| itch.io/app 实页+itch 论坛（4512960/942518/495651） | 检索 2026-10-13 | itch 无官方移动端+侧载摩擦（§5） |
| play.google.com/console/signup；Google Play 12 测试者政策；developer.apple.com（$99/年） | 检索 2026-10-13 | 商店成本（§5） |
| 本仓 `tools/Godot_v4.7.2-stable_mono_export_templates.tpz` 枚举+编辑器二进制符号扫描+GodotSharp.dll 反射 | 实测 2026-10-18 | §1.2/§4.2 全部本仓证据 |
| 本仓 spike1-report.md §1/§3、REV-20261017-T153、FlowSolveBackend.cs、RenderTierMatrix（T-102）、project.godot、47 号文 §1.1 | 既有 | §2/§4 桌面基准与既有适配 |
| **查不到的**：针对本工作负载的公开 Godot-C#-AOT 移动开销基准；itch 移动端官方 app（不存在，仅有桌面 app+非官方 Mitch） | 检索 2026-10-13 | 诚实标注 |

## 8. 交付物与测试

| 交付物 | 路径 | 测试 |
|---|---|---|
| 触控映射纯逻辑 | `game/src/EraCore/Presentation/TouchGestureMapper.cs` | `TouchGestureMapperTests`（6 用例：legal×2/boundary×3+illegal×1——合成事件→决策→既有命令流端到端） |
| 分辨率缩放档纯逻辑 | `game/src/EraCore/Presentation/MobileViewportPolicy.cs` | `MobileViewportPolicyTests`（10 用例：桌面不变/竖屏获益/横屏不换/网格/钳制/非法） |
| 触控输入分支+冒烟 | `game/presentation/EraTD/scripts/BuildInteractionUi.cs`（_GuiInput 触控分支+ApplyTouchDecision+ApplyMobileViewportTier+BUILD_TOUCH 三幕） | headless 冒烟 BUILD_TOUCH 八项自检（Quit(1) 门）+既有 BUILD_OK/BUILD_V2 不变 |
| 本报告 | `docs/engineering/49-t141-mobile-feasibility.md`（本文件） | 文档三件套（版本/日期/状态） |
