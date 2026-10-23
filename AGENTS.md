# AGENTS.md —《我在游戏觉醒系统》项目记忆与工作宪章

> **任何 AI 会话（ZCode/Claude Code 等）在本仓库工作时自动生效。** 本文件是项目的"团队记忆入口"——先读完本文件再动手。
> 最后更新：2026-10-24（v1.2.1 审美打磨已发布：game-visual-taste 技能+T-165）｜维护者：协调会话（业主：hydgame 账号持有人）

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

## 当前进度快照（2026-10-24 v1.2.1 已发布）

Wave 0~9 主体完成（详见 08 号文 v1.17~v1.30 变更记录）：W8 十四项全交付（六时代战役 29 关/联机/程序关/无尽定版/军师三档 L1-L2-L3/TTS 预留/移动评估）；W9 全清（T-154~157/T-160 家族/T-156）；v1.0.0（415591643）/v1.1.0（416649510）/v1.1.1 热修（417252278）三版发布+itch 双轨上线；v1.1.1 教训=出厂冒烟全 headless 的盲区，窗口存活测试入发布流程。**W10 界面美术大修（业主实机抓出"全是文字"立项，08 v1.31）四批全交付**：T-161 主菜单（视频背景/牌匾/六时代色卡/--shot 截图钩子=玩家视角验收承载面）、T-162 选关卡片化（29/29 banner 卡片网格+**出厂降级面修复**：资产根三级链 env>editor>exe旁>walk-up，裸包三证实测）、T-163 战斗 HUD 图标化（92 图标全接/军师立绘 6/F1 抽屉/相机错位大修）、T-164 皮肤手感（七面时代化三态/射程圈/飘字/待机动画/控件图标 10/AttachCues 伪通路修复）。**AI 生图资产 135 件**（logo1+banner31+HUDicon92+立绘6+控件10，vibeapi 业主授权）逐件图像模型验收；母带 172MB 本地存档不入库（.gitignore w10-*-raw）。**v1.2.0 已发布**（Release 421590940：win64/linux64/母片六资产，免登录哈希 MATCH；itch 双通道补丁节省 ~92%；**三道新发版门禁**=窗口存活+玩家视角截图三段+裸包三证全过；08 v1.36/52 号文）。门禁 G1 6967/6967。过程教训：代理 5h 配额两次阵亡于提交前（T-162/T-164——主管代收尾成惯例动作：run_all 复跑+截图亲检+REV 修正案）；SendMessage 失败≠代理死亡（T-161 双代理同树并行幸同源收敛）；会话重启后 shell PATH 漂移（WindowsApps stub 拦 python）。**v1.2.1 审美打磨已发布**（2026-10-24，Release 422046225+itch 双通道：业主令学习 taste-skill+impeccable→技能 game-visual-taste（~/.zcode/skills/，六维审计+像素风家规）→53 号文 12 面审计（18~19/24）→T-165 七件打磨零新生成（字号档/锁遮罩时代暗变体/军师立绘 64 井位/色场垫底/系统件像素皮肤/hover 补全/飘字限流+皮肤过渡）→复评 20+/24 九面无 2 分维。**待业主**：T-158 亲测升级 v1.2.1/T-159 联机真机 15 分钟。挂账池新增 T-165 判断项四件（era 暗边公式单源/tween 双谱系/LineEdit 色键缓存/滚动条像素件）。遗留池：建造栏 e2+ 会话化（架构项）/era5-6 语音行+菜单专属 BGM（冻结判据族）/像素字体/基地伤害面/F-005 难度自选/首败折扣持久化/三份视口同构/SimHost 夹具退役/T-164 判断项四件。外部依赖仅剩：30 人试玩（业主安排真人后恢复 T-062）。

## 沟通口径

对业主（甲方）汇报用通俗大白话；文档与代码用简体中文+英文标识符；甲方已签认：Q8=E1~E4 四章、F-004/F-036 口径、免费+BYOK 定位。
