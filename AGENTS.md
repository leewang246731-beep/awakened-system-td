# AGENTS.md —《我在游戏觉醒系统》项目记忆与工作宪章

> **任何 AI 会话（ZCode/Claude Code 等）在本仓库工作时自动生效。** 本文件是项目的"团队记忆入口"——先读完本文件再动手。
> 最后更新：2026-10-17（W7 收官：E4 章+v1.0 硬验收落库）｜维护者：协调会话（业主：hydgame 账号持有人）

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
| 任务编排（152 任务/波次/并行纪律） | `docs/engineering/08-task-plan.md`（工作队列） |
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

## 当前进度快照（2026-10-18 W8 批 3 收官）

Wave 0~7 主体完成：W7 十一项中九项落库（T-123 E4 内容/T-124 E4 关卡+互联网奇观/T-125 遗迹映射正式版/T-126 四人格全量/T-127 E4 叙事/T-128 E4 美术包/T-130 关闭系统全章硬验收=REV-20261017-v10-fullclear：19 关全 Victory+三跃迁+全成就+警报中性，run_all 全绿 6157 passed；E4 视觉接线（Era4TerrainAtlas/Era4ArtPack+ART4 冒烟）随 W7 收口修复 F1 落地）；T-129 L2 spike/T-131 联机评审经 D-34 业主方针改派 W8（08 v1.15）。T-132 商标检索已归档（45 号文：名称判定可安全使用，历史名 EraTD 禁用）；**v1.0.0 已发布**（tag=d28b1b6，Release 415591643 正式版：win64/linux64/480p 母片包六资产，免登录哈希核对全过，REV-20261017-T133）；唯一在途=itch 轨待业主三步（key/target 后 push_itch.sh+README 补链）。累计 6166+ 测试全绿；E1~E4 四章端到端确定性回放/跃迁事务/无实体无素材清零全部实证。挂账主线：T-095 实测校准随业主终版实测（DEFERRED 同源）；v10 REV 六挂账已归属行尾（08 v1.15 变更记录④）。W8=E5/终章、L2 正式版、TTS、联机、程序关、移动端评估。W8 已开工（业主指令 2026-10-17）：批 1 交付=T-134 E5 未来章全量（era5 五件套/E4→E5 跃迁/CampaignState 清账/戴森云/湮灭/20s 传送带/全托管闸）+T-143 无尽深化（任选时代/混编/奇观带入/滚词缀）；双轴审查修复 3b4a824；套件 6353/6354（唯一红=FlowField 吞吐护栏预存贴线债，T-153 RED-FLAG 挂账，批 2 前先行校准）。批 2 交付=T-135 终章深空全章（六时代集齐：era6 五件套/第五跳/纪元之门/传送阵 ∞/零点能）+T-142 两章美术音频（era5 29+era6 26 贴图+六首 BGM）+T-153 配额校准（6144）；门禁 G1 6483/6483 六步全绿。批 3 交付=T-136 终章叙事与播片（story-e5/e6+信任三档终章台词+关闭系统字幕版+画布 96×112 清账）+T-129 L2 spike（裁决不进主包）；门禁 G1 6511/6511。下一站：批 4（T-137 L2 正式版=可选下载+波间异步+降级 L1+T-138 TTS 预留）。外部依赖仅剩：30 人试玩（业主安排真人后恢复 T-062）。

## 沟通口径

对业主（甲方）汇报用通俗大白话；文档与代码用简体中文+英文标识符；甲方已签认：Q8=E1~E4 四章、F-004/F-036 口径、免费+BYOK 定位。
