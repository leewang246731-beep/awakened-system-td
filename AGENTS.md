# AGENTS.md —《我在游戏觉醒系统》项目记忆与工作宪章

> **任何 AI 会话（ZCode/Claude Code 等）在本仓库工作时自动生效。** 本文件是项目的"团队记忆入口"——先读完本文件再动手。
> 最后更新：2026-10-14（Wave 3 收尾中）｜维护者：协调会话（业主：hydgame 账号持有人）

## 项目一句话

《我在游戏觉醒系统》（Awakened System TD）：生产链塔防 + 六时代战役 + 觉醒系统（游戏内智能体军师，BYOK 接 GLM/MiMo）。Godot 4.7.2 mono + C#（EraCore 纯逻辑库零 Godot 依赖）+ xUnit。**全免费开源**（代码 MIT/资产 CC BY-NC），GitHub 私有仓库存续。

## 不可协商的工作纪律（违反任何一条=返工）

1. **五级测试真实覆盖**：每个开发点必须有单元/逻辑/集成/回归/链路测试（定义见 `docs/engineering/13-quality-gates.md`），断言真实、禁 mock 被测系统、禁恒真断言、禁 happy-path-only。完成前 `bash tools/ci/run_all.sh` 全绿（G1~G4+G2.5）。
2. **code-review 双轴门禁**：每批任务落库后，按 `~/.zcode/skills/code-review/SKILL.md` 流程跑 Standards 轴（`docs/CODE_STANDARDS.md`+`docs/DOCUMENT_STANDARDS.md`）与 Spec 轴（任务卡）双审查→修复→复验，记录归档 `docs/engineering/reviews/`。
3. **反造假红线 15 条**：`docs/engineering/02-consistency-audit.md` §红线 + CI 红线扫描器 `tools/ci/redline_scan.py`（9 检查，R5 密钥/R7 锚点常量/T8 金样流程等）。硬编码绝对路径/伪逻辑/假成功/本机路径=红线。
4. **金样纪律**：金样更新必须独立 commit + `--update-golden` 入口留档（CS-6）。
5. **D 盘纪律**：一切下载/缓存/数据库只在 D 盘（`DOTNET_ROOT=D:\dotnet`、`NUGET_PACKAGES=D:\nuget`、Godot 在 `D:\game\tools\godot_extract`）；密钥只存 `config/`（gitignored）或 DPAPI，永不入库入日志。
6. **并行度**：最多 2 个实现代理并行；审查错峰；性能门禁（Spike1/SimGrouping）只在安静窗口跑（2026-10-14 纪律）。
7. **每任务一 commit**：message 首行含任务号（T-xxx）；`git add` 只加自己路径（禁 -A）；并行冲突后提交者消解。
8. **数值从注册表来**（RL7）：游戏数值活在 `game/content/**/*.json`，产品代码禁硬编码锚点值（`ci-allow` 豁免须 REV 登记）。
9. **确定性是底线**：任何 Sim 改动保持"同 seed+命令流→哈希全等"；金样 `game/content/golden/` 漂移即红。
10. **诚实汇报**：测试失败/跳过/环境抖动必须如实标注原因，禁止伪造绿记录（先例：多代理因"假理由"被审查勘误）。

## 文档地图（新会话按需读）

| 要读什么 | 去哪 |
|---|---|
| 游戏设计方案（唯一真相源 v3.2.x） | `docs/我在游戏觉醒系统-游戏设计方案.md` |
| 任务编排（147 任务/波次/并行纪律） | `docs/engineering/08-task-plan.md`（工作队列） |
| 代码/文档标准（审查依据） | `docs/CODE_STANDARDS.md` + `docs/DOCUMENT_STANDARDS.md` |
| 质量门禁 G1~G10 | `docs/engineering/13-quality-gates.md` |
| 决策记录（32 裁决+历轮审查） | `docs/engineering/07-decided-plan.md` + `docs/engineering/reviews/REV-*` |
| 特性追溯（F-001~177→任务） | `docs/engineering/01-feature-inventory.md` + 08 附录 A |
| 规格（敌表/卷轴/三重目标/12 意图…） | `docs/engineering/11-spec-tables.md` |
| 二开基础（素材/彩蛋/养料经济） | `docs/engineering/21-secondary-dev-base.md` + 18/19/20 号研究 |
| 新机迁移 | `docs/engineering/23-migration-plan.md` |

## 环境速查（D 盘纪律）

```
export NUGET_PACKAGES='D:\nuget' DOTNET_ROOT=/d/dotnet
dotnet=/d/dotnet/dotnet.exe
Godot=/d/game/tools/godot_extract/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe
一键门禁=bash tools/ci/run_all.sh（性能门禁留安静窗口跑）
视频生成 skill=~/.zcode/skills/ark-video（成本护栏：非测试档须 --i-know-the-cost；成片落 U 盘 E:\game-videos）
密钥=config/{secrets.local.json, ark_key.txt, gh_token.txt}（vibeapi 生图/MiMo/ark/GitHub）
```

## 当前进度快照（2026-10-14 晚）

Wave 0~2 全部完成；Wave 3 完成 17/22（在途：批 3 修复轮/T-079；待做：T-078 E2 美术/T-085 关卡集/T-084 自动通关回归→出口核验）。累计 90+ 提交、4400+ 测试全绿基线、确定性回放/恢复等价/跃迁事务/19 动作端到端全部实证。Wave 4=v0.1 内测包（E1+E2 可玩），之后 W5 Web Demo/W6 E3/W7 E4+v1.0。外部依赖仅剩：30 人试玩（业主安排真人后恢复 T-062）。

## 沟通口径

对业主（甲方）汇报用通俗大白话；文档与代码用简体中文+英文标识符；甲方已签认：Q8=E1~E4 四章、F-004/F-036 口径、免费+BYOK 定位。
