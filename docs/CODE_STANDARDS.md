# 代码规范（CODE_STANDARDS）

| 项 | 内容 |
|---|---|
| 版本 | v1.1 |
| 日期 | 2026-10-14 |
| 状态 | 已冻结（任务 T-026，随 Day-0 契约包，07 决议 D-30） |
| 适用范围 | `game/` 下全部 C# 与 GDScript 代码、`tools/ci/` 工具脚本 |
| 效力 | code-review **Standards 轴**依据（与 `docs/DOCUMENT_STANDARDS.md` 并列；08 §3 任务纪律第 5 条） |
| 依据 | 06 §4.2 六节骨架；07 决议 D-01~D-32；02 一致性审计 RL1~RL15 |
| 变更纪律 | 与 K-1~K-12 契约同纪律管理：变更=独立任务+版本递增+评审 |

> **可检验性声明**：每条规则带编号 CS-x.y 并注明"检验"——`R#/T#` 指 `tools/ci/redline_scan.py`（T-027）检查项，"守卫测试"指 `dotnet test` 内反射断言，"review" 指 code-review Standards 轴逐条判定。不可检验的表述不收录。已实现检查项：R9/T1/T2/T4/R5/R7/R13/T8/T3（W1 收口，9 条；R1 经 R7 覆盖）；其余（R2-R4/R6/R8/R10-R12/R14/R15/T5-T7）随其对象落地启用（06 §3.1 总表）。

---

## 1. 分层与端口纪律（架构守卫）

- **CS-1.1 EraCore 零引擎依赖（D-01）**：`game/src/EraCore` 禁引用 GodotSharp，禁以 Godot 类型出现在公开 API。检验：`GodotSharpGuardTests` 反射断言（EraCore.Tests/Architecture）+ review。
- **CS-1.2 EraCore 禁动态反射（D-05，AOT 前置）**：禁 `System.Reflection.Emit*`、`Assembly.Load`；序列化只用 System.Text.Json source generator。检验：守卫测试（banned assemblies）+ `Directory.Build.props` 的 `EnableAotAnalyzer`。
- **CS-1.3 EraCore 禁 Newtonsoft 与直连网络（D-01/D-20）**：禁 Newtonsoft.Json、禁直接 `System.Net.Http`；真实网络调用仅存在于测试程序集的 HttpClient 实现。检验：守卫测试 + review。
- **CS-1.4 时间与随机必须经端口**：墙钟经 `IClock`、随机经 `IRng` 注入；EraCore 内禁 `DateTime.Now`/共享 `Random`。检验：review（引用扫描）。
- **CS-1.5 事件总线同步单线程（D-12）**：tick 内按触发序同步派发，事件带 `(tick, seq)`；音频/UI 接力经 Godot 适配器 `call_deferred`。检验：K-8 契约测试 + review。
- **CS-1.6 GDScript 仅表现层**：`game/presentation/` 是唯一 res:// 根（D-02）；业务逻辑禁入 presentation，逻辑全下沉 EraCore。检验：review；T7（后续）路径扫描。
- **CS-1.7 目录布局（D-02）**：`src/EraCore`、`src/EraCore.Tests`、`presentation/`、`contracts/`、`data/`（gitignored+.gdignore，运行时可写目录不进 res:// 导入扫描与导出清单）。检验：review（结构 diff）。
- **CS-1.8 工具链钉死一处（D-04）**：TFM=net8.0 由 `game/Directory.Build.props` 唯一钉死，禁 netstandard2.x；Godot 4.7.2-stable mono 全解决方案对齐。检验：review（props/csproj diff）。

## 2. 命名与注册表模式

- **CS-2.1 C# 命名**：类型/公开成员/常量 PascalCase；接口 `I` 前缀；私有字段 `_camelCase`；命名空间与目录路径一致。检验：review。
- **CS-2.2 一文件一主类型**：文件名=主类型名（T2 映射依赖此约定）。检验：review。
- **CS-2.3 数值唯一经注册表（RL7）**：塔基准/乘子表/研究成本/敌种/波次参数只存 `data/**/*.json`，引擎只读表；禁在 C# 硬编码任何设计数值（F.2/F.5 系）。检验：R7（随 C-29 数据表落地启用）+ 金样回归。
- **CS-2.4 动作唯一经注册表（RL6）**：全部 19 动作必须经动作注册表+Schema+预算扣点执行，无旁路、无 `--bypass-budget` 类测试后门。检验：R6 反射守卫（随 C-03/C-14 落地启用）。
- **CS-2.5 ContentType+aliases 注册表（K-2）**：内容类型识别唯一入口；模组与本体同管线。检验：schema 矩阵测试（T-020 起）。

## 3. 测试规范

- **CS-3.1 测试三行必备（合法/边界/非法）**：每个被测行为必须同时覆盖三类输入，`[Theory]` 表驱动并用注释节标注 `// legal`、`// boundary`、`// illegal`；非法行断言"确实拒绝了"（具体异常/返回值），不止"不抛异常"。检验：review（DoD-2/13；范式样例 `GuardTests.cs`）。
- **CS-3.2 测试与 F-ID 关联注释**：每个测试方法须带 `// [F-xxx]`（特性编号）或 `// [C-xx]`（组件编号）注释，置于方法属性区或体内；亦可在**测试类声明处**做类级标注（其下方法全部视为已关联），或用 `[Trait("Feature", "F-xxx")]`。检验：**T3**（WARNING 级覆盖率报告；新增测试缺失关联=review MAJOR）。
- **CS-3.3 断言质量**：断言具体值/状态/事件序列；禁恒真（`Assert.True(true)`）、字面恒等式、两参同表达式的自反断言、无任何断言的测试方法、空 `catch {}`；浮点断言带容差并注明容差来源。检验：**T4** + review（DoD-8）。
- **CS-3.4 禁 mock 核心逻辑**：mock/Fake 白名单=七端口（IClock/IRng/IFileStore/IPersistPort/IHttpPort/ISecretStore/IJobPort）+ 跨层接口（IIntentParser/IChatProvider）；被测系统本身禁被 mock；cassette 必须由录制器生成，禁手写（D-20）。检验：T5（后续）+ review（DoD-4）。
- **CS-3.5 负向用例**：每个公开不变式至少 1 条"故意违反应被拦截"用例。检验：review（DoD-13）。
- **CS-3.6 测试运行环境**：EraCore 的单元/逻辑/组合级集成测试全部跑 `dotnet test` 不启动 Godot；Godot 层测试占比目标 <20%（D-13）。检验：CI 分级过滤 + review。
- **CS-3.7 src↔test 文件映射**：`game/src/EraCore` 每个源文件须有对应测试文件（`<Stem>Tests.cs` 或 `<Stem><Suffix>Tests.cs`）；纯契约接口/纯数据文件须在 `tools/ci/allowlist.txt` 以 `T2:` 条目显式声明（带理由）。检验：**T2**。
- **CS-3.8 测试临时数据**：测试可写数据入 `data/.tmp-tests/` 或系统临时目录并在 Dispose 清理；禁向仓库提交运行产物。检验：review（范式样例 `DevFileStoreTests.Dispose`）。

## 4. 禁例黑名单（假成功/假占用防治；06 §2.3 + RL 述）

- **CS-4.1 禁本机绝对路径（RL9）**：代码/测试/配置禁盘符绝对路径；游戏资源用 `res://`、可写数据经 IFileStore 注入根；环境事实唯一登记处=`docs/engineering/07-decided-plan.md` §三（附）。检验：**R9**。
- **CS-4.2 禁残留标记**：交付代码注释禁 TODO/FIXME/HACK/XXX/WIP/stub/placeholder/占位/待补/暂不实现/伪实现/假成功。检验：**T1**。
- **CS-4.3 禁密钥入代码（RL5）**：密钥只经 `config/secrets.local.json`（gitignored）或环境变量；永不入代码/日志/战报/cassette/崩溃报告。检验：**R5** + DOCUMENT_STANDARDS #5。
- **CS-4.4 禁固定值冒充计算（RL1/RL7）**：波次/伤害/首解率等输出禁直接返回设计文档期望值；金样必须由数据 JSON 驱动引擎产出。检验：金样回归（T-035 起）+ review。
- **CS-4.5 禁 mock/跳过冒充流场（RL2）**：流场测试禁 Skip/Mock/注入旧路径缓存；性能计数器必须真实采样。检验：R2（随 C-11 落地启用）。
- **CS-4.6 禁假占用**：性能与异步测试禁 `Thread.Sleep`/空循环凑时间；帧时间采样基于真实 tick 计时。检验：review。
- **CS-4.7 禁 console 冒充战报（RL10）**：五段管线内禁 `GD.Print`/`Console.WriteLine` 输出替代 JSONL 落盘。检验：R10（随 C-13 落地启用）。
- **CS-4.8 禁 headless 帧率冒充 60fps（D-19）**：CI 只报逻辑预算（sim tick/流场 P99），60fps 判定仅真机档。检验：性能报告口径 review。
- **CS-4.9 禁演出层另行随机（RL12）**：奖励先定后演，演出仅回放。检验：事件序断言（T-087）。
- **CS-4.10 禁越权实现/漏项**：实现须逐条对齐任务卡验收标准原文，禁自造宽松标准。检验：code-review Spec 轴。

## 5. 资源寻址与数据布局

- **CS-5.1 资源寻址**：游戏资源只允许 `res://` 相对寻址且仅存在于 presentation/ 内；禁 `file://` 与盘符路径。检验：**R9** + review。
- **CS-5.2 可写数据根注入（D-03）**：saves/reports/mods 一律经 `IFileStore`（开发机根可经 `ERA_DATA_ROOT`/构造注入覆盖；发布=exe 旁 `saves\`；Web=浏览器持久化 FS）；禁依赖 `user://` 默认值（落 C 盘，D 盘纪律违约点）。检验：**R9**（字面）+ review（行为）。
- **CS-5.3 data/ 隔离**：`game/data/` 为 gitignored+.gdignore 运行时目录，不进导入扫描与导出清单。检验：review。
- **CS-5.4 D 盘纪律**：一切下载/缓存/可写数据仅 D 盘；`DOTNET_ROOT`/`NUGET_PACKAGES`/`GODOT_BIN` 等环境值以 07 §三（附）登记为准，代码内不重复硬编码。检验：**R9** + 环境事实登记 review。

## 6. 金样更新流程（回归资产纪律；06 §1.4 / §3.1 T8）

- **CS-6.1 显式再生成**：金样文件只可经 `--update-golden` 开关重新生成，禁手改 `golden/**` 内容。
- **CS-6.2 独立 commit**：金样变更 commit 禁同时包含 `src/**` 产品代码变更（防"改金样凑绿"）。**首版**金样允许与产生它的引擎代码同 commit 合入（因果：引擎先于金样存在），但必须在 REV 记录登记；**更新**金样必须独立 commit（原条款不变）。检验：T8（已启用，W1 收口；首版同引擎合入须 REV 登记 + allowlist `T8: <sha>` 条目豁免，先例 d6f5862 见 REV-20261013-wave1-round3；更新金样无豁免通道）。
- **CS-6.3 依据留痕**：金样 commit message 必须含任务 ID + 依据编号（A#/N#/D# 或裁定出处）。
- **CS-6.4 过审合入**：金样更新必须过 code-review 后方可合入；1000 波统计摘要须落容差带内。

> `golden/` 已建立（T-035 wave-e2、T-040 sim-e1 两件首版）；本节自首个金样任务（T-035）起生效，流程冻结。首版偏差（d6f5862 同引擎合入）经 CS-6.2 首版条款登记豁免，见 allowlist `T8:` 条目与 REV-20261013-wave1-round3。

## 7. 豁免与例外流程（D-18）

- **CS-7.1 行级豁免**：违规行本行或其上一**纯注释行**加 `// ci-allow: <检查ID> <任务ID 理由>`（XML 配置文件在注释块内用纯文本尾注），且豁免必须在审查记录（`docs/engineering/reviews/`）中引用。检验：redline_scan 豁免机制 + review。
- **CS-7.2 路径级豁免**：`tools/ci/allowlist.txt` 逐条登记（`路径 | 理由`；`T2:` 前缀把豁免限定到单检查；`路径 :: 节标题` 只豁免该节内行）。检验：allowlist diff 属 100% 人工评审范围。
- **CS-7.3 现行白名单**（T-027 首次登记）：`tools/**`（扫描器自身）、`.gitignore`、`docs/engineering/07-decided-plan.md`（决议书，环境事实唯一登记节=§三（附））、已冻结讨论/审计/设计归档（01/02/03/04/06/08 与方案主文档、逆向摘要）——逐条理由见 allowlist.txt。**代码内的已登记事实**（`DevFileStore.DefaultDevRoot`、`nuget.config` 的 NUGET_PACKAGES 注释、DevFileStoreTests 的 D-03 断言）使用行级 ci-allow 标注。

## 8. code-review Standards 轴用法

1. 逐 CS-x.y 判定 BLOCKER/MAJOR/MINOR；**CS-4.x 红线类违反即 BLOCKER**（06 §4.3 分级）。
2. 扫描器结果是下界：redline_scan PASS 不免除 review 责任（启发式扫不全处——CS-1.4/1.6/3.1/3.5/4.4~4.10 等——由 review 兜底）。
3. 规则↔检查映射：CS-1.1~1.3↔守卫测试；CS-3.2↔T3；CS-3.3↔T4；CS-3.7↔T2；CS-4.1/5.1/5.2/5.4↔R9；CS-4.2↔T1；CS-4.3↔R5；CS-2.3/4.4↔R7+金样（R7 已启用，W1 收口）；CS-2.4↔R6（后续）；CS-4.5↔R2（后续）；CS-4.7↔R10（后续）；CS-6↔T8（已启用，W1 收口）；CS-7↔allowlist/ci-allow 审查。

---

## 附录：范式样例（既有代码即范本）

- 测试三行必备 + 注释节标注：`game/src/EraCore.Tests/GuardTests.cs`
- F-ID/C-ID 关联（方法级与类级）：`GuardTests.cs`（类级 `[C-01]`）、`DevFileStoreTests.cs`（方法级 `[C-01]`）
- 架构守卫测试：`game/src/EraCore.Tests/Architecture/GodotSharpGuardTests.cs`（`[F-154]`）
- 行级豁免标注：`game/src/EraCore/IO/DevFileStore.cs`（D-03 登记根，`ci-allow: R9`）

## 变更记录

- v1.1（2026-10-14，W1 收口）：CS-6.2 补首版澄清条款（首版金样允许与产生它的引擎代码同 commit 合入——因果：引擎先于金样存在；须 REV 登记，先例 d6f5862；更新金样仍必须独立 commit）；§6 尾注更新（golden/ 已建立）；头部已实现检查清单 7→9（R7/T8 上线，R1 经 R7 覆盖）；§8 映射行同步。出处=REV-20261013-wave1-round3（MAJOR CS-6.2 裁决）。
- v1.0（2026-10-13）：T-026 首次成文。依 06 §4.2 六节骨架+07 D-30 裁决：八节 33 条可检验规则（含架构守卫/命名注册表/测试三行必备/F-ID 关联注释格式/金样更新流程/豁免流程），并登记 T-027 扫描器首批 7 检查项映射。
