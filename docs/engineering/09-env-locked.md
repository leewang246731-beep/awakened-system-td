# 09 环境快照登记（本机钉死工具链）

| 项 | 内容 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-10-13 |
| 状态 | **生效**（code-review round 1 处置 #12 新增；与《07-decided-plan.md》§三（附）环境事实登记同源，本文件为其展开快照+复现命令） |
| 用途 | 新 shell/新代理开工前的环境对齐清单；07 §三（附）仍是纪律裁决的唯一登记处，本文不引入新事实类别 |
| 上游 | 07-decided-plan D-03/D-04、§三（附）；REV-20261013-wave0-round1 |

## 一、钉死工具链（全部 D 盘，实测核验 2026-10-13）

| 项 | 登记值 | 实测 |
|---|---|---|
| .NET SDK | `D:\dotnet\dotnet.exe`，版本 **8.0.425**（`dotnet --version` 实测） | 本 shell 无 PATH dotnet，须全路径或先 `export PATH=/d/dotnet:$PATH` |
| Godot 编辑器 | `D:\game\tools\godot_extract\Godot_v4.7.2-stable_mono_win64\Godot_v4.7.2-stable_mono_win64.exe`（console 变体同目录 `_console.exe`，headless/CI 用它） | `Godot Engine v4.7.2.stable.mono.official.ed1daf0bf` 冒烟通过（REV §四④） |
| Godot 自带 nupkg | 同目录 `GodotSharp\Tools\nupkgs\`（Godot.NET.Sdk / GodotSharp / Godot.SourceGenerators / GodotSharpEditor，全部恰 4.7.2） | 由 `tools/ci/setup_local_nuget.sh` 镜像进本地 feed |
| 本地 NuGet feed | `D:\game\tools\nuget-local\`（`game/nuget.config` 源 `godot-local-4.7.2` 指此） | 4 个 nupkg 已镜像实测 |
| NuGet 包缓存 | `NUGET_PACKAGES=D:\nuget` | 任何 restore/test 前必须 export（禁默认 `%USERPROFILE%\.nuget` C 盘缓存） |
| 导出模板 | `D:\game	ools\godot-templates.7.2.stable.mono\`（T-096 装载） | Godot 4.7.2 无 `--export-templates-path` 参数（CLI help 实查），模板字节落 D 盘登记位，经 NTFS junction `%APPDATA%\Godot\export_templates` → `D:\game	ools\godot-templates` 让编辑器按默认查找路径命中（junction 是指针，C 盘零字节，D 盘纪律保持）；tpz 原包 1.2 GB 留 `D:\game	ools\` |
| 密钥 | `D:\game\config\secrets.local.json`（gitignored；MiMo key） | 存在已验；内容不读取、不入日志（RL5） |
| 保底启动器 | `D:\game\tools\Godot-D盘运行时启动器.bat`（set DOTNET_ROOT=D:\dotnet 后拉起编辑器） | 用于用户级变量失效时的手动兜底 |

## 二、环境变量

| 变量 | 值 | 级别 | 说明 |
|---|---|---|---|
| `DOTNET_ROOT` | `D:\dotnet` | **用户级（HKCU\Environment，2026-10-13 设置，reg query 已验）** | 解决 Godot(mono) 报 `Unable to load .NET runtime/hostfxr`：Godot 需凭它定位 .NET 8 运行时。先于该设置启动的 shell 看不到——`tools/ci/smoke_godot.sh` 已内置兜底导出；手动场景用启动器 bat |
| `NUGET_PACKAGES` | `D:\nuget` | 会话级（按命令 export） | D 盘纪律；尚未设用户级，故意为之（CI 显式注入更可审计） |
| `ERA_DATA_ROOT` | 未设 | — | 未设时 `DevFileStore` 落到 `DefaultDevRoot=D:\game\game\data`（D-03 优先级：注入参数 > 环境变量 > 钉死默认） |

## 三、复现命令（Git Bash，仓库根 D:\game）

```bash
# 1) 全量测试（round 1 实测 37/37 绿）
export NUGET_PACKAGES='D:\nuget'
/d/dotnet/dotnet.exe test game/EraTD.sln

# 2) 红线扫描 + 自测（round 1 实测 7 检查 0 FAIL 0 WARN；selftest 19 passed）
python tools/ci/redline_scan.py
python tools/ci/test_redline_scan.py

# 3) 镜像本地 NuGet feed（幂等，4 个 nupkg）
bash tools/ci/setup_local_nuget.sh

# 4) Godot headless 冒烟（构建 presentation + import + main.tscn，判据=输出含 SMOKE_OK）
bash tools/ci/smoke_godot.sh
```

说明：脚本均按仓库相对路径解析并拒绝 C 盘检出；`smoke_godot.sh` 会兜底导出 `DOTNET_ROOT`（见 §二）。Windows 侧保底入口：双击 `D:\game\tools\Godot-D盘运行时启动器.bat`。

## 四、变更记录

- v1.0（2026-10-13）：首次成档（code-review round 1 处置 #12）；DOTNET_ROOT 用户级设置当日完成并实测（smoke 通过）。
