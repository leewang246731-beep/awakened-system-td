# REV-20261013-wave0-round1 — Wave 0 批次 code-review 双轴发现与处置

| 项 | 内容 |
|---|---|
| 审查对象 | Wave 0 批次提交（基线 `4daab34` T-026+T-027 及其工作副本改动） |
| 审查日期 | 2026-10-13 |
| 双轴 | Standards（CODE_STANDARDS/06 质量门禁）× Spec（08-task-plan 任务卡验收） |
| 处置执行 | 修复执行代理（本 commit）；全部发现逐项处置，见 §二 |
| 验证 | §四四项全过（测试 37/37、红线 0 FAIL 0 WARN、扫描器自测 19/19、Godot 冒烟 SMOKE_OK） |

## 一、发现总览

MAJOR ×4（含 1 项挂账处置）、MINOR ×7、Godot 环境事件 ×1。无驳回项：全部发现本轮修复或挂账登记。

## 二、逐项处置表

### MAJOR

| # | 发现 | 处置 | 落点 |
|---|---|---|---|
| M1 | `DevFileStore.Resolve` 用裸 `StartsWith(root)` 做包含检查——同盘前缀兄弟目录（`<root>X`）与混合分隔符可逃逸注入根 | 路径先 `Path.GetFullPath` 规范化，与 `root.TrimEnd(分隔符)+分隔符` 比较（根自身仍放行）；越界一律 `UnauthorizedAccessException` 失败关闭。新增负例：`../data-evil/x.bin`、`..\data-evil\x.bin`、`../../escape.bin`、真实落盘的 `<root>X` 兄弟目录三种形态全部必须抛错 | `game/src/EraCore/IO/DevFileStore.cs`；`DevFileStoreTests.Relative_path_escaping_root_is_rejected` / `Same_drive_absolute_path_sharing_the_root_prefix_is_rejected` |
| M2 | `GodotSharpGuardTests` 守卫无负向对照——若扫描逻辑恒空返回，"EraCore 零 GodotSharp"断言空洞成立（假绿风险） | 扫描逻辑重构为静态方法 `AssemblyGuard.FindForbiddenReferences(assemblyPath, forbidden)`（PE 元数据 AssemblyReferences 表直读，按路径审计任意程序集文件）。负向对照①：对 EraCore.Tests 自身程序集禁用 "xunit"（自身确已引用 xunit.core）断言能找出（守卫会红）；②：禁用 "GodotSharp" 断言找不出（不误报）；③强对照：对 EraTD presentation 程序集断言 GodotSharp 被找出（已构建实测咬合；dll 缺席时静默通过以防环境裸机红） | `game/src/EraCore.Tests/Architecture/AssemblyGuard.cs`（新增）；`GodotSharpGuardTests` 三个 negative-control 测试 |
| M3 | `game/EraTD.sln` EraTD 项目 `Release\|Any CPU.{ActiveCfg,Build.0}` 误映射到 `Debug\|Any CPU`——Release 构建实为 Debug 产物 | 两行均改 `Release\|Any CPU`；三个项目映射全核对无误 | `game/EraTD.sln` |
| M4 | 《10-recovered-v21-architecture.md》头部任务映射错位一格（遗迹映射标 T-002、19 动作表标 T-003、提示系统标 T-004） | 头部与 §二/§三/§四 逐一改为：遗迹映射=T-003、19 动作表=T-004、提示系统=T-005；§一 T-002 保持仅 §16 技术架构；文末变更记录补 v1.0.1 更正说明一行 | `docs/engineering/10-recovered-v21-architecture.md` |

### MAJOR（挂账处置）

| # | 发现 | 处置 | 落点 |
|---|---|---|---|
| M5 | T-027 验收口径"23 检查脚本+七段流水线从零跑通一次"，实际 Actions 流水线未做、检查仅落地 7 条 | 不冒领：08-task-plan T-027 行尾挂 `[DEBT-W0: Actions 七段流水线未做，W0 出口条件⑤核验时扣住；剩余 16 检查随对象落地]`；本轮以本地四件套（dotnet test / redline_scan / 自测 / Godot 冒烟）充当出口证据；本 REV 即处置记录 | `docs/engineering/08-task-plan.md` T-027 行；本文件 |

### MINOR

| # | 发现 | 处置 | 落点 |
|---|---|---|---|
| m1 | `GuardTests.cs` 一文件两类（CS-2.2 违例） | 按文件名拆分（改动小者）：`GuardClampTests.cs` + `GuardEnsureInRangeTests.cs`，原 `GuardTests.cs` 删除 | `game/src/EraCore.Tests/` |
| m2 | `DevFileStoreTests` 缺"文件不存在必须抛错"非法用例；无 legal/boundary/illegal 分节；Dispose 删共享父目录；期望根硬编码工作站字面量 | 补 `ReadAllBytes_of_missing_file_throws`（FileNotFoundException/DirectoryNotFoundException 均为 IOException 派生，断言收窄到基类）；`// legal/boundary/illegal` 三分节注释；Dispose 只删本类 GUID 子目录；期望根改由 `ERA_DATA_ROOT ?? DefaultDevRoot` 推导（`DefaultDevRoot` 为既有 public const，未新加属性） | `game/src/EraCore.Tests/IO/DevFileStoreTests.cs` |
| m3 | 悬空引用：`smoke_godot.sh` 在 Main.cs 注释与 08 任务卡中被引用但不存在；Godot nupkg 镜像无脚本 | `.gitignore` 放行 `!/tools/ci/`（其余 /tools 载荷仍忽略）；新建 `setup_local_nuget.sh`（编辑器自带 4 个 nupkg 镜像到本地 feed `tools/nuget-local/`——`game/nuget.config` 既有源 `godot-local-4.7.2` 所指目录；拒绝 C 盘检出）与 `smoke_godot.sh`（console exe `--headless --path` 先 `--import` 后跑主场景，grep SMOKE_OK，300s 兜底超时，DOTNET_ROOT 兜底导出）。两脚本均真实执行通过（§四④） | `.gitignore`、`tools/ci/setup_local_nuget.sh`、`tools/ci/smoke_godot.sh` |
| m4 | 《05-schedule-discussion.md》两处引用不存在的"04-implementation-plan" | 改为 `07-decided-plan.md`（状态行 + §6.2 标题） | `docs/engineering/05-schedule-discussion.md` |
| m5 | 归档文档状态头不齐：02 缺版本/状态，04/08 缺状态行 | 02 补"版本 v1.0/状态 已冻结归档"；04 补"状态 已冻结归档"；08 补"状态 已冻结"（含 DEBT-W0 提示） | 对应三文件头部表 |
| m6 | allowlist 对 07-decided-plan 整文件 R9 豁免过宽（正文裁决行不应整卷放行） | 收窄为节级：`R9: docs/engineering/07-decided-plan.md :: 三（附）、环境事实登记`（节级豁免=标记行起至下一 `## ` 标题前，语法见 redline_scan.py 豁免机制）；正文 D-02/D-03/D-22 三处含路径裁决行改行级 `ci-allow: R9` 显式登记。实测扫描仍 0 FAIL | `tools/ci/allowlist.txt`；07 三处行内尾注 |
| m7 | 环境事实散落（Godot 报 hostfxr 不可加载的解法无登记处） | 新建环境快照登记 `09-env-locked.md`：dotnet 8.0.425@D:\dotnet、Godot 4.7.2-stable mono、NUGET_PACKAGES=D:\nuget、DOTNET_ROOT 用户级、保底启动器、MiMo key 位置、附四条复现命令；allowlist 配套 R9 豁免 | `docs/engineering/09-env-locked.md`；`tools/ci/allowlist.txt` |

### Godot 环境事件（非代码缺陷，登记备查）

| 事件 | 事实与处置 |
|---|---|
| Godot(mono) 无头运行报 `Unable to load .NET runtime/hostfxr` | 根因：Godot 需定位 .NET 8 运行时而本机 dotnet 不在 PATH 且 `DOTNET_ROOT` 未设。处置：2026-10-13 设用户级环境变量 `DOTNET_ROOT=D:\dotnet`（HKCU\Environment，reg query 已验）；`smoke_godot.sh` 内置兜底导出（先于变量启动的 shell 也能跑）；手动兜底入口 `D:\game\tools\Godot-D盘运行时启动器.bat`。登记于 09-env-locked.md §二 |
| Godot.NET.Sdk 构建输出重定向 | presentation 实际输出在 `game/presentation/EraTD/.godot/mono/temp/bin/Debug/EraTD.dll`（无 TFM 段），非 `bin/Debug/net8.0`。守卫测试 FindEraTdAssemblyPath 双路径探测 |

## 三、随修附带（非 review 发现）

- `main.tscn` 脚本引用大小写 `res://scripts/main.cs` → `res://scripts/Main.cs`（协调者修复，随本 commit 一并入库；Windows 大小写不敏感文件系统下可跑，Linux/导出会断）。
- `EraTD.csproj` 显式补 `TargetFramework=net8.0`（Godot.NET.Sdk 项目下 Directory.Build.props 传递不确定，显式钉死防漂移）；构建残留 `EraTD.csproj.old` 清除不入库。

## 四、完成门槛验证（全部真实执行，摘要原文摘录）

① **全量测试**（`export NUGET_PACKAGES='D:\nuget'`；`/d/dotnet/dotnet.exe test game/EraTD.sln`）：

```
已通过! - 失败:     0，通过:    37，已跳过:     0，总计:    37，持续时间:  58 ms - EraCore.Tests.dll (net8.0)
```

（37 = 基线 31 + 新增 6：逃逸负例 2、守卫负向对照 3、缺文件抛错 1。首轮曾红 1 例——缺文件用例断言 FileNotFoundException 而实测缺目录抛 DirectoryNotFoundException，已按 IOException 契约修正后全绿；守卫强对照经 `--filter` 单跑复核 1/1 通过，EraTD.dll 内含 GodotSharp 引用串已 grep 验证，非空转跳过。）

② **红线扫描**（`python tools/ci/redline_scan.py`）：

```
[PASS] R9   绝对/盘符路径扫描（RL9）
[PASS] T1   残留标记扫描（TODO/FIXME/stub/占位/假成功）
[PASS] T2   src↔test 文件映射
[PASS] T4   恒真/空断言扫描
[PASS] R5   密钥模式扫描（RL5）
[PASS] R13  git 基线可达性（RL13）
[PASS] T3   测试 F-ID 关联覆盖率（WARNING 级）  23/23 方法已关联 (100%)
summary: 7 checks | 0 FAIL | 0 WARN | exit_code=0
```

③ **扫描器自测**（`python tools/ci/test_redline_scan.py`）：`selftest summary: 19 passed, 0 failed`。

④ **Godot 无头冒烟**（`bash tools/ci/smoke_godot.sh`，首次 import，退出码 0）：

```
smoke_godot: 已兜底导出 DOTNET_ROOT=D:\dotnet
smoke_godot: 构建 presentation（EraTD，含 EraCore 引用）… 已成功生成。 0 个警告 0 个错误
smoke_godot: 首次资源 import（可重复执行）…
smoke_godot: 无头运行主场景（main.tscn → Main._Ready）…
Godot Engine v4.7.2.stable.mono.official.ed1daf0bf - https://godotengine.org
SMOKE_OK core=0.0.1-wave0
smoke_godot: SMOKE_OK 已捕获——运行时/程序集/EraCore 链路三合一通过
SMOKE_EXIT=0
```

（SMOKE_OK 由 `Main._Ready` 打印，含 EraCore.VersionInfo 版本串，证明 mono 运行时+托管程序集+EraCore 引用链三合一。）

## 五、遗留与移交

- DEBT-W0（M5）：Actions 七段流水线 + 剩余 16 条红线检查——W0 出口条件⑤核验时扣住，不随本轮关闭。
- T-030 导出模板目录 `tools/godot-templates/` 仍空（09 §一已标注"先勿做导出类任务"）。
- gdUnit4Net Day-0 冒烟（T-029 后半）未做：本轮 smoke 走的是 Godot 原生 headless + 自研断言串路径，gdUnit 裁决仍待 T-029 正式任务。
