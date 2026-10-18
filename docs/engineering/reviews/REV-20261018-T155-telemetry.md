# REV-20261018-T155 — 时长遥测（实测校准批 T-158 的前置）

| 项 | 内容 |
|---|---|
| 任务 | T-155（08 v1.24 W9 批 1；行 228：时长遥测（实测校准前置），E 级，验收=**每关/每局真实用时自动落本地 JSONL（玩家零操作）；E1/E3/E5/终章/程序关/无尽六类全覆盖；含关卡类型/seed/结果字段**） |
| 特性 | F-006（无尽/时长域）｜C-19（引擎） |
| 依据 | 08 v1.24 裁定③「时长口径=遥测先行（T-155）+亲测脚本（T-158）三账同清」；AGENTS 纪律 8（数值/观测从注入面来）/9（确定性是底线）；T-140 时长口径先例（通关刻 tick/30/60）；T-077/T-109/T-138 设置页披露行先例；D-03（IFileStore 注入根）/CS-1.4（墙钟经 IClock）/D-05（source-gen 序列化） |
| 交付物 | `EraCore/Telemetry/LevelDurationTelemetry.cs`（记录器+绑定助手）；`LevelSession`/`LevelSessionConfig` 挂点接线；`SettingsFormState` 披露文案+设置页 `TelemetryNoticeRow`（scene+panel+smoke 判据）；测试 `LevelDurationTelemetryTests`（18 项单元）+`LevelDurationTelemetryChainTests`（六类链路）+`SettingsFormStateTests` 增量；本 REV |
| 环境 | 本机开发机（D 盘纪律：`NUGET_PACKAGES=D:\nuget`、`DOTNET_ROOT=/d/dotnet`）；零网络零上传（本地 JSONL 面） |

## 一、口径裁定（本 REV 核心——暂停扣除与否的裁定与依据）

**双口径记账，主口径=纯游玩 tick 数，墙钟为参考列：**

| 口径 | 定义 | 暂停处理 | 断点重进处理 | 用途 |
|---|---|---|---|---|
| **主口径 PlayTicks** | 本坐会话内模拟实际步进的 tick 数（`EndTick − StartTick`，K-1 30Hz 固定 tick 域） | **天然扣除**（Paused 态不步进） | 只计重进后步进（`Resumed=true` 行可拼接还原整关） | T-085 估算器校准/程序关 40~90 带复核/T-158 三账同清 |
| **参考口径 WallMs** | `EndMs − StartMs`（注入墙钟差，CS-1.4 经 IClock.NowMs——LlmGateway CallLog 同先例） | 含暂停（真人节奏参考） | 从重进时刻起算 | 真人 vs 机器节奏比对、挂机/暂停占比审计（`WallMs − PlayTicks×33.3ms`） |

**裁定依据**：①确定性是底线（AGENTS 纪律 9）——tick 数同 seed+命令流可复算，墙钟受宿主帧率/暂停/挂机影响不可复算，故校准账（T-085/T-140/T-158）必须以 tick 为主；②与 T-140 程序关时长口径同源（`通关刻 tick / 30 / 60 = 分钟`，10 局中位 54.80min 即此口径）——遥测行 PlayTicks 可与该表直接对账；③墙钟差异是真人行为信息（暂停多久/挂机多久），丢弃可惜——双记不弃。**建议项（任务卡原文）"纯游玩 tick 数"被采纳为主口径，正是确定性友好之选。**

**终局口径**：Victory（战役/程序关胜波场清）/Defeat（DeclareDefeat 或无尽死亡终局——无尽无胜利终态）/Abandoned（战役中途退出 `SaveAndExitToMenu`/无尽 `Abandon`）。弃局=**本坐诚实终态**：可续关的退出不是"没发生过"，重进续关产生 `Resumed=true` 新行，同 `levelId+seed` 相邻行拼接还原整关真实总用时（T-158 消费侧口径）。

## 二、挂点说明（零侵入证明）

**不改 sim 热路径**——全部挂点在既有结算钩/会话建立点，`Simulation.Step` 内部零改动：

| 类别 | 挂点 | 事件 |
|---|---|---|
| 战役关 E1~E6 全部 | `CampaignLoop.LevelSession`（每关必经的 C-12 编排器） | 开始=会话建立（tick 基线=`Sim.CurrentTick`，重进=退出刻）；结束=既有三钩：`OnVictory`（胜利结算钩）/`DeclareDefeat`（T-057 失败档钩）/`SaveAndExitToMenu`（中途退出钩） |
| 程序关 | 同上（T-140 装载管线零改动——程序关装载后就是 LevelSession） | 同上 |
| 无尽局 | `EndlessTelemetry` 绑定助手（入口组装位） | 开始=`EndlessEntry.Begin` 调用侧 `BeginRun`；结束=`Die()`/`Abandon()` 调用侧 `tracker.End`；tick 台账=Σ 每已开波 `IntervalSeconds×30`（`WaveLedgerTicks`，确定性波形派生） |

**注入式启用（缺省关死）**：`LevelSessionConfig.Telemetry`（scope 声明，缺省 null）+ `LevelSession(..., durationTelemetry: null)`（服务注入，缺省 null）——**两者齐备才记录**，全部既有调用面（6000+ 测试/存档链）零影响（单元测试 `LevelSession_with_telemetry_writes_on_defeat_and_stays_silent_without_scope` 对照组钉死）。无尽挂点选入口绑定而非改 `EndlessEntry.Begin` 签名的理由：无尽域无自身时钟（波计划+记分域，实时节奏归宿主），tick 台账由宿主驱动的波流派生——遥测随入口组装一次性接上，`EndlessEntry`/`EndlessRun` 零改动零哈希面影响（本批 T-154 并行触同文件，避交叉账）。

**headless 自动局照记**：`Source` 列分账（`player`/`script`）——脚本局数据同样入账（六类链路测试本身就是 headless 自动局，9 行全 `source=script`），供 T-158 比对机器 vs 真人比率。

## 三、JSONL 行格式（schema v1）

路径：注入根下 `telemetry/durations.jsonl`（**相对路径**——dev=`data\telemetry\`，发布件=exe 旁注入根 `saves\` 同级，Web=浏览器 FS；D-03 任意盘纪律，越出注入根拒收）。累计追加（不截断）、LF 结尾、一行一 JSON 对象（K-8 journal 同纪律）。真实行样例（六类链路实测产物同形）：

```json
{"Schema":1,"Kind":"Campaign","LevelId":"campaign:level-e1-01","EraIndex":1,"Seed":20261101,"Outcome":"Victory","Source":"script","Resumed":false,"StartTick":0,"EndTick":115541,"PlayTicks":115541,"StartMs":1760000000000,"EndMs":1760000026532,"WallMs":26532}
```

字段：`Kind`（Campaign/Procedural/Endless）｜`LevelId`（关卡域 id；无尽=`endless` 哨兵）｜`EraIndex`（时代 1~6；无尽=开局时代）｜`Seed`（replay key #1）｜`Outcome`（Victory/Defeat/Abandoned）｜`Source`（player/script）｜`Resumed`｜`StartTick`/`EndTick`/`PlayTicks`（主口径）｜`StartMs`/`EndMs`/`WallMs`（参考口径；未注入墙钟=0，tick 口径不受影响）。消费入口=`LevelDurationTelemetry.ReadAll(IFileStore)`（坏行 loud fail）。

序列化=source-gen `TelemetryJsonContext`（D-05 AOT 红线）；枚举落名字符串（K-8 先例）。

## 四、设置页玩家可见性（数据流向声明）

「时长遥测（本地）」披露行（T-138 TtsReservedRow 同构：纯 Label、灰置、无任何可点击控件）：
- 文案（`SettingsFormState.TelemetryNoticeZh`，逐字上屏）：「每关/每局用时自动记入本机数据目录 telemetry/durations.jsonl（仅用于校准关卡时长）；该文件不上传、不联网，随时可手动删除。」
- 口径同 BYOK 先例（T-109 三档对比条/L1 行「不发送任何数据」）：**本地文件、零上传零联网、删除即清除**；路径只述相对段（真实根=宿主注入盘，发布件任意盘纪律）。Web 档位行下披露不隐藏（披露不随表单隐藏——T-109 先例）。smoke 判据：行可见+文案逐字+行内无 BaseButton（SETTINGS_OK 行含 `telemetry=local-notice-ok`）。

## 五、测试账（五级中的单测/链路级）

| 件 | 覆盖 |
|---|---|
| `LevelDurationTelemetryTests`（18 项，legal/boundary/illegal 三段标注） | 追加语义（一坐一行/累计/残缺尾行修复）/双口径独立性/弃局/来源分账/无墙钟降级/JSONL 单行合法性/注入根落位与隔离（发布件数据目录纪律）/LevelSession 挂点+缺省关死对照/零 tick 终局/墙钟回拨钳 0/双终局拒绝/负 tick 拒绝/坏 scope 拒绝/越根拒收/坏行 loud fail |
| `LevelDurationTelemetryChainTests`（六类链路） | E1~E6 六时代真内容真命令流通关+程序关（T-140 生成器产物）+无尽局（真内容死亡终局）+战役弃局行——9 行账本逐行断言（kind/era/levelId/seed/outcome/PlayTicks>0/EndTick=终局刻/物理形态 LF+行数） |
| `SettingsFormStateTests` 增量 | 披露文案三要素（本地/不上传/可删）+不述盘符/绝对路径 |

## 六、诚实边界与挂账

1. **表现层实时宿主未接 CampaignLoop**（既有事实，非本任务引入）：当前 presentation 的 SimHost 跑的是 raw Simulation 沙盒演示（无关卡生命周期钩）；真实玩家局的记录在战役宿主接入 `LevelSession`（T-157 表现层打磨/后续 C-21 接线域）时随注入自动点亮——挂点/链路已由六类链路测试实证，宿主接入时零引擎改动。
2. **墙钟参考列在 headless 局只反映跑批墙钟**（如六类链路 wallMs=秒级而 tick 口径=64min——headless 快于实时是真实现象不是错账）；真人局的墙钟列待真实宿主注入真钟后生效。
3. **无尽 PlayTicks=波形台账口径**（确定性派生）与真人局实际 tick（含波间操作停留）存在正常差——差值即墙钟列的参考价值；若后续无尽宿主接真实 tick 源，可在宿主侧改喂实际 tick（`tracker.End` 参数本就开放）。
4. **多坐拼接未做聚合视图**：`Resumed` 行拼接逻辑留给 T-158 消费脚本（遥测只保证原始行诚实）。
