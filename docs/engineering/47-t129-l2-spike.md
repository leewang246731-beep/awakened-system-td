# T-129 L2 llama.cpp 两周集成 spike 报告 —— 本地推理（3B Q4）意图填充实测与进包结论

- **任务编号**: T-129（L2 llama.cpp 两周集成 spike，时间盒，评估性质；08 任务表 W8 批 3）
- **执行日期**: 2026-10-13（全部实测在本日完成；spike 代理 ZCode）
- **版本**: v1.0（首版）
- **状态**: 已完成（代码零改动；仓库唯一落点=本报告）
- **验收口径**: 08 任务表 T-129 行——"3B Q4 完成一次意图填充+进包与否结论文档"
- **数据流向声明**: 本 spike 全部推理在本机 llama.cpp server（127.0.0.1:8017）完成，**零云端调用、零数据外发**；模型文件来自 HuggingFace 公开直下（开源许可，见 §1.2）。正式版若循此路线同样为纯本地数据流，天然规避 L3 云端的 Coding Plan 白名单限制问题（L3 侧该限制仍按 `docs/engineering/13-quality-gates.md` 既有口径执行）。

---

## 0. 一句话结论

**不推荐进主包，推荐维持 T-137 口径（模型可选下载、不进主包）**：3B Q4 在工具调用通道（=LlmGateway 真实对接面）上 3/3 产出 K-3 合法意图→计划 JSON（其中 1 条依赖既有 F-096 ①b 别名规整——该防线恰好为这种回显而设计），纯 CPU 热缓存整答 3.9~22.7 s、冷首答 82.5 s、常驻内存 ~3.4 GB，而模型体积 1.93 GB ≈ 主包（170~180 MB）的 11 倍——格式能力达标但体积/延迟/内存三账皆不支持默认分发。

---

## 1. 环境与版本（全部 D 盘，C 盘零占用）

### 1.1 硬件/系统口径（诚实记录：本机无独显 NVIDIA，全部数据为纯 CPU 推理）

| 项 | 值（来源：PowerShell CIM 实测） |
|---|---|
| CPU | Intel Core i5-10210U @ 1.60 GHz，4 物理核 / 8 逻辑线程 |
| 内存 | 15.8 GB |
| GPU | Intel UHD Graphics（核显）+ AMD Radeon RX 640（无 CUDA/ROCm 可用面）→ **推理口径=纯 CPU（-t 4，物理核数）** |
| 系统 | Windows 11 家庭中文版 10.0.26200 |
| D 盘余量 | 实测时 ~187 GB free |

### 1.2 软件栈

| 组件 | 版本/来源 | 落点 | 大小 | 许可 |
|---|---|---|---|---|
| llama.cpp | **b11714**（0.7.0-dev，commit c0fd78626，Clang 20.1.8 win-x64），GitHub Releases 预编译 `llama-b11714-bin-win-cpu-x64.zip`（CPU 版，非 CUDA/Vulkan） | `D:\llama-cpp\b11714\` | 18.75 MB（zip）/ ~66 MB（解压后） | MIT |
| 模型 | **Qwen2.5-3B-Instruct Q4_K_M GGUF**（bartowski 量化镜像，HuggingFace 公开直下） | `D:\models\Qwen2.5-3B-Instruct-Q4_K_M.gguf` | **1.93 GB**（1929903264 字节，下载后尺寸校验一致） | Qwen2.5 = Apache-2.0（许可友好，满足任务卡"开源许可友好"要求） |
| 下载耗时 | 模型 4 分 13 秒（~9.4 MB/s 家宽口径） | — | — | — |

- 下载纪律核对：两处落点均在 D 盘仓库外（`D:\llama-cpp\`、`D:\models\` 新建），未触碰 `tools/` 白名单（`tools/*` 的 .gitignore 白名单仅 ci/art/release，本 spike 的临时探针脚本用后即删，见 §5 局限）。
- server 启动参数：`llama-server.exe -m <gguf> --port 8017 -c 4096 -t 4 --jinja --host 127.0.0.1`（`--jinja` 启用内建聊天模板+工具调用解析；n_slots=4 默认并行槽）。

---

## 2. 意图填充实测（三条军师意图 prompt 全文+原始输出）

### 2.1 对接面与判据

- **意图层口径**：`game/src/EraCore/Contracts/Intents/IntentKind.cs` 的 12 意图枚举（Build/FocusFire/SetAlarm 等）；
- **动作层判据**：19 封闭动作的 K-3 字段表（`game/src/EraCore/Agent/Actions/ActionSchemaTable.cs`）——判"格式合法"= JSON 可解析 + `action` ∈ 19 封闭动词 + 必填字段齐 + 类型/边界（BuildPlan cells 1..6 项 {x:int,y:int}；FocusFire target_id int≥1；SetAlarm after_seconds∈[30,600] 钳制域 + reason 非空串）+ 未知字段拒收（JsonSchemaValidator 同口径）；
- **两种喂法**（同一 server，`temperature=0`，`max_tokens=200`）：
  - **Mode A（直答 JSON）**：系统提示含 19 动词清单+字段说明，要求"只输出一个 JSON 对象"；
  - **Mode B（工具调用，=LlmGateway 真实对接面）**：`POST /v1/chat/completions` 携带 `tools` 数组——系统提示与用户军令包裹均逐字取自 `game/src/EraCore/Agent/Pipeline/LlmActionRouter.cs`（SystemPrompt 原文 + `<untrusted_order>` 包裹，F-096 ① 注入防线），tools JSON 按 `BuildToolsJson()` 的线形从 K-3 表逐动词构造。
- **三条军令**（era1 词汇表实词，塔 id 取自 `game/content/era1/towers.json` 真实注册表）：

| # | 军令原文（user 消息体= `<untrusted_order>` 包裹后的原文） | 期望动作 | 期望计划 JSON |
|---|---|---|---|
| P1 | 在东门建两座箭塔，坐标 (12,5) 和 (13,5) | BuildPlan | `{"action":"BuildPlan","tower_id":"era1:tower-wood-arrow","cells":[{"x":12,"y":5},{"x":13,"y":5}]}` |
| P2 | 兽崽冲进东门了，全军集火 3号敌人 | FocusFire | `{"action":"FocusFire","target_id":3}` |
| P3 | 两分钟后提醒我升级箭塔 | SetAlarm | `{"action":"SetAlarm","after_seconds":120,"reason":"升级箭塔"}`（reason 为自由串，K-3 仅要求非空） |

### 2.2 Mode A（直答 JSON）原始输出——0/3 合法

两轮运行（冷/热各一）输出**逐字节一致**（temperature=0 确定性口径）。原始输出全文：

```json
P1 → {"BuildPlan":{"tower_id":"era1:tower-wood-arrow","cells":[{"x":12,"y":5},{"x":13,"y":5}]}}
P2 → {"HeroOrder":{"order":"skill","target_id":3,"skill":"全军集火"}}
P3 → {"action":"UpgradeTower", "target_id":"era1:tower-wood-arrow", "to_tier":2, "after_seconds":120}
```

判读：P1 语义内容全对但**包成了 `{动词:{…}}` 嵌套形，缺 `action` 判别字段**（K-3 闸必拒）；P2 选错动词（HeroOrder 而非 FocusFire）且同样嵌套形+未知字段；P3 选错动词（UpgradeTower）+ 混入 after_seconds 未知字段。**结论：3B Q4 靠自然语言说明跟随精确 wire 格式不可靠（0/3），意图理解本身也只在 P1 全对。**

### 2.3 Mode B（工具调用=游戏真实面）原始输出——**3/3 合法（含①b 规整），raw 2/3**

```json
P1 → tool_call BuildPlan({"tower_id": "箭塔", "cells": [{"x": 12, "y": 5}, {"x": 13, "y": 5}], "confirm_overwrite": true})
P2 → tool_call FocusFire({"target_id": 3})
P3 → tool_call SetAlarm({"after_seconds": 120, "reason": "升级箭塔提醒"})
```

判读（逐条对 K-3）：
- P1：动词/cells/坐标全对；`tower_id` 回显**别名「箭塔」**而非注册表 id——`LlmActionRouter.Canonicalize`（F-096 ①b"字符串经封闭词典规整"防线，别名词典 `era1:tower-wood-arrow: ["木箭塔","箭塔","arrow tower"]` 含此面）**恰好按设计改写为注册表 id → 规整后 K-3 全字段合法**。本条是 spike 的意外收获：既有防线天然兜住本地小模型的别名回显，无需新增代码。
- P2：全字段合法（raw 直接过闸）。
- P3：动词对、after_seconds=120 ∈ [30,600] 钳制域、reason 非空串（"升级箭塔提醒"与期望串不同属自由串字段，K-3 合法；raw 直接过闸）。
- **正确率口径**：raw（无①b）2/3；经游戏既有①b 规整 **3/3**。

### 2.4 延迟与内存实测表（单位见列头；来源=探针脚本 wall-clock + server 日志 print_timing + Get-Process）

| 指标 | 数值 | 口径/来源 |
|---|---|---|
| 模型加载（进程启动→listening） | 3.2 s / 4.7 s（两次启动） | server 日志时间戳 |
| **冷首答（tools 面，前缀未缓存）** | **82.52 s**（prompt 1781 tok 全量处理，~21.6 tok/s） | 探针二轮独立重启后首请求 |
| **热整答（tools 面，前缀已缓存）** | **3.91 s（P2，21 gen tok）~ 22.7 s（P1，59 gen tok）**；P3=11.0 s | cached_tokens=1780/1781，两轮热测一致 |
| 生成速率 | 3.0~5.1 tok/s（160~332 ms/tok） | server print_timing |
| 流式首 token（TTFT，热缓存） | 1.05 s | SSE 流式单测 |
| Mode A 整答（对照） | 5.2~8.9 s（热，prompt 仅 ~430 tok） | 探针 |
| **llama-server 常驻内存** | 工作集 **3321→3435 MB**（加载后→压测后）；私有提交 1.64→1.67 GB | Get-Process（-c 4096 × 4 槽口径，单槽可降） |
| 首轮离群记录（诚实标注） | 首轮 P1 出现过 23.4 s（Mode A）/104.5 s（Mode B）离群，判为冷缓存+后台负载叠加；以重启后独立冷测 82.5 s 与两轮热测一致值为准 | 探针日志两轮对照 |

**关键机制发现**：llama.cpp server 自动缓存共享前缀（系统提示+19 工具 schema ≈1780 tok）——同前缀后续军令只付 17~32 tok 增量处理，这是热整答能进 4~23 s 区间的先决条件；**每局首次军令若不预热即 ~80 s 级**，进包方案必须含"启动后台预热一次"（正式版 T-137 的"波间异步"口径天然兼容）。

### 2.5 OpenAI 兼容端点对接面实测（任务卡要求实测一条）

- `POST http://127.0.0.1:8017/v1/chat/completions`（携带 `tools`/`tool_choice`/`stream`/`temperature`/`max_tokens`）：返回标准 OpenAI 线形（`choices[].message.tool_calls[].function.{name,arguments}` + `usage`，流式为 SSE 分片 + `[DONE]`）——**与 `game/src/EraCore/Llm/OpenAiProtocol.cs` 的 `BuildEndpoint`（`{base}/chat/completions`）+ SSE 归约（SseLineBuffer）面直接兼容**，LlmGateway 只需一条 provider 行指向 `http://127.0.0.1:8017/v1`（本地无密钥），无需协议改动；
- 附带核过 `GET /v1/models`（模型列举）与 `/health`（健康探测）均可用（`LlmConnectionProbe` 探活面可用）。

---

## 3. 进包与否结论（spike 主体裁决）

### 3.1 三本账

| 账 | 实测值 | 对主包（现 170~180 MB，v1.0 win64 母片口径）的影响 |
|---|---|---|
| **体积账** | 模型 1.93 GB + llama.cpp CPU 版 ~66 MB（解压后） | 约为现主包的 **11 倍**；任何"进主包"方案都使母片从 ~180 MB 涨到 >2 GB，毁掉 itch 免登录哈希核对的轻量分发面（`docs/engineering/46-v100-release-notes.md` 的分发资产口径） |
| **延迟账** | 热 3.9~22.7 s / 冷 82.5 s / TTFT 1.05 s（纯 CPU，i5-10210U 低压 U 口径） | 对"军令→意图→计划"交互链：L1 文法 <1 ms（既有 C-15）承担模板军令；L2 只对 L1 澄清的自由军令有价值，但 4~23 s 热整答（+首答 80 s 预热坑）意味着**只能做波间异步军师**，不能做即时意图填充 |
| **内存账** | server 常驻工作集 ~3.4 GB（-c 4096×4 槽；压到单槽小上下文可估 ~2.5~3 GB 级，未单独实测，标注为估） | 游戏+Godot 本体再叠加 3 GB 级常驻，对 8 GB 低配机逼近红线；16 GB 本机实测无压力但风扇满转（观察记录） |

### 3.2 裁决

1. **不进主包**（体积一票否决：11 倍主包；延迟/内存两账亦不支持默认开启）；
2. **维持 T-137 既有口径**：模型**可选下载**（游戏内"启用本地军师"引导二次下载 ~2 GB 至用户数据目录，或玩家自放 GGUF 自行指向路径），llama.cpp 侧优先"运行时拉起 `llama-server.exe` 子进程 + LlmGateway 指向 `http://127.0.0.1:PORT/v1`"的进程外方案——本 spike 已实测该对接面零协议改动可用（§2.5）；GDExtension 进程内方案（F-163 原案）留 T-137 评估，本 spike 不背书（未测）；
3. **若进包（可选下载）的强制配套**（移交 T-137 验收面）：①启动即后台预热一条哑请求（消 82.5 s 冷首答）；②下载完成前 L2 档隐藏，失败/超时**降级 L1 路径已在**（K-5 管线 AgentPipeline 的 L1→澄清卡既有路径，无需新增）；③单槽小上下文（-c 2048/-np 1）压内存；④`--jinja` 必开（工具调用解析依赖）；
4. **意外收获**：F-096 ①b 别名规整防线恰好兜住 3B 模型的别名回显（P1 实证），本地小模型接入无需新增防线代码。

### 3.3 与任务卡验收的对照

"3B Q4 完成一次意图填充"= **达成**（Mode B P2/P3 raw 过闸 + P1 经既有①b 规整过闸，3/3 K-3 合法）；"进包与否结论文档"= 本报告 §3。

---

## 4. 局限（诚实声明）

1. **单机口径**：全部数据出自一台 i5-10210U 低压笔记本（纯 CPU）；台式机/新架构 CPU（AVX512 等）生成速率会显著更高，本报告不外推。首次运行曾现 23~104 s 离群（冷缓存+后台负载），已以独立重启冷测+两轮热测一致值记账。
2. **样本 3 条 × 2 轮**：spike 时间盒口径，非统计显著；且三条均"意图明确"型军令，未测歧义/对抗注入样本（注入防线面 F-096 ① 已按原样随附，但未构造攻击样本验证）。
3. **Mode A 失败不等于"直答 JSON 路线死刑"**：llama.cpp 另有 `response_format` JSON Schema 强约束（structured output）未测；但游戏真实面（LlmGateway）走 tools 通道，Mode B 已达标，此项不影响裁决。
4. **内存单槽口径未实测**（估记于 §3.1 表注）；模型仅测 Qwen2.5-3B Q4_K_M 一档，未测 Q3/Q2 体积档与 1.5B 档。
5. **临时探针脚本已删**（`tools/t129_llm_probe.py` 等，用后即删不入库）；复现所需全部信息（版本/参数/prompt 全文/判据）已收录于本报告 §1~§2。llama.cpp 与模型留在 `D:\llama-cpp\`、`D:\models\`（D 盘，共约 **2.0 GB**，仓库外）供 T-137 复用。
6. **08/AGENTS 未动**；本 spike 代码零改动，仓库落点仅本文档。

---

## 变更记录

- v1.0（2026-10-13，T-129）：首版。环境/版本、三条 prompt 全文+原始输出、延迟/内存实测表、OpenAI 兼容对接面实测、进包裁决（不进主包/维持 T-137 可选下载口径）、局限声明。
