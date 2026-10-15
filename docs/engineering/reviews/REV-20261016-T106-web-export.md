# REV-20261016-T106-web-export — HTML5 导出+Web 约束（引擎阻断实证）

| 项 | 内容 |
|---|---|
| 任务卡 | T-106（HTML5 导出+Web 约束；Wave 5，F 泳道，组件 C-31/C-26；覆盖 F-144/F-164；前置 T-100~T-102 已合入 6a2e22d/eeb449b/1c69c75） |
| 验收（08 行原文） | threads-off 包无 SharedArrayBuffer 依赖+L2/L3 禁用断言 |
| 状态 | **F-164（Web 约束代码路径+断言）全量交付**；**F-144（HTML5 导出产物）被钉死引擎硬拒——Godot 4.7.2-mono 不支持 C#→Web 导出，本任务不可伪造产出**，证据与决策选项见 §2/§5 |
| 提交 | （单 commit，见 §7） |

---

## 1. 交付物清单（全部可测、已测）

1. **`game/src/EraCore/Presentation/WebConstraints.cs`**（新）：平台事实（`OS.HasFeature("web")`）→ 三档可用性表 `AgentTierAvailability(L1Grammar, L2LocalModel, L3CloudByok)` 的唯一决策点（能力表驱动）：Web=L1 恒开+L2/L3 禁（§17 条 15），桌面=三档全开。消费点（设置页可见性/网关平台门/冒烟断言）一律读本表，不自建 if。
2. **`game/src/EraCore/Llm/LlmGateway.cs`**：新增构造参数 `cloudTierEnabled`（默认 true=桌面行为逐字节不变）；false 时 `RouteAsync` 在**其余所有门之前**（provider 存在性→enable→白名单→Key→重试矩阵）拒绝，错误键 `web-tier-disabled`，零网络、零 Key 访问、零重试、仍记调用日志行（F-091 审计完整）。
3. **`game/src/EraCore/Llm/LlmErrorCatalog.cs`**：新键 `web-tier-disabled` 中英双语文案（与 `disabled` 区分——平台约束≠玩家设置）。`LlmErrorCatalogTests` 的全键完备性守卫自动覆盖新键（键集↔文案集一一对应即绿）。
4. **`game/presentation/EraTD/scripts/SettingsPanelUi.cs`**（F-164 设置面）：
   - Instantiate 期以 `WebConstraints.Availability(OS.HasFeature("web"))` 装定 `_tiers`；
   - Web 下隐藏**全部 BYOK（L3）表单面**（含 Key 输入/测试连接/拉取列表/声明页脚），仅留画质档位行（非 BYOK 字段），并加常驻告知 `WebTierNoticeZh`（T-110 的完整 Web UI 适配在其卡内继续）；
   - `ProbeAsync` 走网关平台门（`cloudTierEnabled: _tiers.L3CloudByok`）；`FetchModelsAsync` 因 /models 绕过网关直用 wire 端口，在面板侧同键拒绝（零 wire 调用）。
5. **`game/presentation/EraTD/scripts/Main.cs`**（冒烟 5j 段）：`RunWebSmoke()` 打印 **WEB_OK**——(1) 三档表 Web 行/桌面行双双断言+live 平台与 L3 门一致性；(2) L3 平台门走**真实 LlmConnectionProbe 文案链**：默认模板激活行（enable=true）+Key 在库的最强场景仍 `web-tier-disabled` 零网络拒绝（wire 端口实现为"被调用即抛"哨兵——不可达即证明）；(3) T-100 存档探针 Web 事实（e_sqlite3 不可用注入）→ `JsonFileSavePort` 回落+`json-fallback` Warn 记录；桌面真探针结果**只观测不断言**（`live_save_probe=sqlite`，Godot mono 宿主内原生库解析依环境而异，探针语义即"运行时决定"）。失败=WEB_FAIL+Quit(1)（先于 SMOKE_OK，即红冒烟门禁）。
6. **`game/src/EraCore.Tests/Presentation/WebConstraintsTests.cs`**（新，4 用例：legal×2/boundary×2）+ **`game/src/EraCore.Tests/Llm/LlmGatewayWebGateTests.cs`**（新，5 用例：最强场景零网络零 Key/NULL provider 先于存在性门/SendAsync 路径 Attempts=1/调用日志行/桌面默认不受门影响；`NeverPort`+`AccessCountingSecretStore` 为判据 oracle）。
7. **`game/presentation/EraTD/export_presets.cfg`**：preset.1 "Web"（`variant/thread_support=false`=threads-off，F-144 契约；全量纹理压缩关=最大兼容；`canvas_resize_policy=2` 自适应）。头部注释完整登记引擎阻断实证（见 §2）——preset 为引擎支持落地当日（或未来 GDScript demo 切片）的即用配置。
8. **`tools/release/build_web.sh`**（新，build_v020 同源环境纪律）：dotnet build（零警告）→ Godot import → `--export-release "Web"` → **导出被拒=打印引擎原文+exit 1（反造假红线：不产伪包）**；若产出则自动执行：no-SAB 验证（产物 grep `SharedArrayBuffer|crossOriginIsolated|coi-serviceworker` 必须零命中——threads-off ⇒ 不需 COOP/COEP 头）→ 本地 `python -m http.server:8000` + curl HEAD（index.html 200+页面内全部 `src/href` 引用逐一 200）→ zip+sha256+U 盘同步 `E:\game-videos\releases\web-preview\`。

## 2. 引擎阻断实证（F-144 核心无法达成的证据链）

1. **官方 mono 导出模板无 Web 件**：`tools/Godot_v4.7.2-stable_mono_export_templates.tpz` 27 条目逐一枚举（python zipfile），**零** `web*.zip`（对照：编辑器二进制字符串表内含 `web_release.zip`/`web_dlink_release.zip`/`web_nothreads_release.zip`/`web_dlink_nothreads_release.zip` 等完整模板名清单——即引擎认识这些模板名，mono 包就是不发）。
2. **编辑器硬拒（本项目）**：`--headless --export-release "Web"` 报
   `ERROR: Cannot export project with preset "Web" due to configuration errors: Exporting to Web is currently not supported in Godot 4 when using C#/.NET. Use Godot 3 to target Web with C#/Mono instead. / 如果这个项目不使用 C#，请使用非 C# 版本的编辑器来导出项目。`（editor_node.cpp `_fs_changed`；英文字符串在 `EditorExportPlatformWeb` 验证器内，二进制实证）。
3. **编辑器硬拒（对照实验：与 C# 无关）**：另建纯 GDScript 单场景最小项目同命令导出——**同样被拒**（同文案）。结论：mono 编辑器对任何项目都导不出 Web（指示改用非 C# 编辑器）；而 EraTD 全部游戏逻辑是 C#（EraCore 60+ 文件+presentation 15 脚本），非 mono 编辑器既装不了 C# 也无 .NET glue，无可用路径。
4. **上游佐证**：godotengine/godot#70796（"Readd support for web platform exports when using C#"）——官方文档同口径"Godot 4 C# 项目当前不能导出 Web"；2025-05 GodotCon 曾展示 Web .NET 原型（未进 stable）。**本仓证据以钉死的 4.7.2-mono 编辑器实测为准。**
5. **模板路径 junction 已覆盖但无关**：`%APPDATA%\Godot\export_templates`→D 盘 junction（T-096 先例）在位——阻断不在模板查找层，在编辑器导出平台验证层（早于模板检查）。即便手工放入非 mono 包的 `web_nothreads_release.zip`（官方非 mono tpz 内有），mono 编辑器对 C# 项目仍拒绝（见第 2/3 条），故不做该无效装载。

## 3. F-144/F-164 验收口径逐条核对

| 验收原文 | 状态 | 证据 |
|---|---|---|
| threads-off 包无 SharedArrayBuffer 依赖 | **引擎阻断未产包**（§2）；验证器已内置（build_web.sh 第 4 步，产物 grep 零命中门禁） | 导出拒绝原文+脚本 |
| L2/L3 禁用断言 | **全量交付** | WEB_OK 冒烟（Godot 进程内三档表+门+回落三断言）+ 9 条单测全绿 |
| F-144"任意静态托管直接可玩（无特殊响应头）" | 设计侧就绪：threads-off preset+COOP/COEP 不需证明注记（WEB_OK 行 `coep=not-required(threads-off)`；nothreads 模板按 Godot 4 文档不含 SAB/COI 引用，build_web.sh 产物级验证就位） | export_presets.cfg+脚本 |
| F-164"Web 构建中 L2/L3 入口隐藏且代码路径禁用，仅 L1 可用" | 代码路径已交付并断言；"Web 构建"本体待引擎（§2）。L1 天然离线（L1IntentParser 纯解析），Web 下军令语法继续可用 | §1.4/§1.5+单测 |

## 4. 本轮坑与解法（T-096 格式）

- **CS1061 陷阱**：`LlmTransportResult` 只有 `(ActionJson, ErrorKey)` 两字段（无 `Ok`）——与 `LlmGatewayCallResult` 不同名不同形，测试断言按 null/键值写。
- **xUnit1030**：测试方法内 `ConfigureAwait(false)` 触发警告（G3 TreatWarningsAsErrors 即红）——测试内一律裸 await（库代码内保留 false 不受此限）。
- **/models 绕过网关**：`LlmModelList.FetchAsync` 直用 wire 端口（不经 LlmGateway），平台门必须在其调用点（SettingsPanelUi.FetchModelsAsync）另置同键拒绝——仅网关门不够。
- **桌面真探针勿断言方向**：Godot mono 宿主内 e_sqlite3 原生库解析依环境而异（本轮实测 `live_save_probe=sqlite` 解析成功）；SavePortFactory 语义本就是"运行时决定"，冒烟只断言 Web 事实注入路径（回落+Warn），桌面路径只观测。
- **`tee` 吃退出码的测量陷阱**：`bash script | tail; echo $?` 取的是 tail 的码——验证脚本退出码必须直接 `bash script; echo $?`（本轮实测自纠）。
- **并行泳道现场**：T-104 同刻在途（JsonSchemaValidator/ModActionParamSpace*未提交态曾现 CS8604 警告与编译错），属其泳道文件，本泳道未触碰；run_all 以其提交后状态为准。

## 5. 决策请求（业主/协调会话裁定，W5 出口"陌生人点开链接即玩"受制于此）

| 选项 | 内容 | 代价 |
|---|---|---|
| A | **升钉引擎版本**至官方 C#→Web 支持落地的 4.x stable（跟踪 #70796；届时换 mono 模板+可能需 wasm-tools workload），preset/build_web.sh/全部 Web 约束代码即日生效 | 需 D-04 变更+全量回归；时间表在上游 |
| B | **GDScript Web demo 切片**（非 mono 编辑器导出纯 GDScript 壳，玩法为 L1 军令+基础塔防的独立轻实现） | 双实现维护（薄≠假红线需严格把守）； itch 页面（T-107）先上桌面包+demo 页 |
| C | **W5 口径改桌面分发**（itch HTML5 槽位缺位，T-107/T-110 相应收缩为桌面+文档声明 Web 约束已备） | 验收 W5-①"点开链接即玩"需改口径 |
| D | Godot 3 mono 分支导 Web | 违 D-04 工具链钉死+C# 代码库不兼容，**不建议** |

## 6. 冒烟与门禁证据

- `bash tools/ci/smoke_godot.sh`：`WEB_OK tier-table=l1-only-on-web l3-gate=web-tier-disabled save-web-fallback=json warn=logged live_save_probe=sqlite render-tier-web=auto-L(T-102) coep=not-required(threads-off)` + `SMOKE_OK core=0.0.1-wave0 scale=1`（全量 marker 段照旧全绿）。
- `dotnet test --filter WebConstraints|WebGate|ErrorCatalog`：15/15 绿（含既有目录完备性守卫）。
- `python tools/ci/redline_scan.py`：9 检查 0 FAIL 0 WARN（T2 映射含新文件、T3 关联 100%、R5 密钥——合成 smoke key 与既有 `SmokeKey` 同范式）。
- `bash tools/release/build_web.sh`：**预期红**——步骤 3 打印引擎拒绝原文后 exit 1（SCRIPT_EXIT=1 实测），步骤 4~6 未达（无产物可验证，诚实降级）。
- **run_all（提交前）**：`G1=5408 passed/0 failed（239s）｜G2 红线 9 检查 0 违规｜G2.5 资产 515 文件 0 拒绝｜G2.6 模组 3 装载 0 失败｜G3 零警告｜G4 SMOKE_OK`——**总体 PASS**。诚实记录：首轮 run_all 曾红在 `Spike1PerformanceTests.MazeComposite300Enemies1000ProjectilesLogicBudget`（中位逻辑帧 3.226>3.0ms、sim p99 4.491>4.0ms）——时值 T-104 泳道并行构建/测试（其轮询环每 ~3 分钟重建全解决方案），性能门禁墙钟受扰；按 2026-10-14 纪律"性能门禁只在安静窗口跑"于 T-104 提交（b476984）后复跑，同代码全绿（本泳道改动零 sim/性能路径，两次运行代码相同）。

## 7. 提交与复现

- commit：`T-106: html5 export (threads-off, no-SAB, L2L3-off, web smoke)`（单 commit；`git add` 仅本泳道路径：EraCore 三文件+EraCore.Tests 两文件+presentation 三文件+tools/release/build_web.sh+本 REV）。
- 复现：`bash tools/ci/smoke_godot.sh`（看 WEB_OK）｜`bash tools/release/build_web.sh`（看引擎拒绝原文+exit 1）｜`dotnet test game/EraTD.sln --filter "FullyQualifiedName~WebConstraints|FullyQualifiedName~WebGate"`。
