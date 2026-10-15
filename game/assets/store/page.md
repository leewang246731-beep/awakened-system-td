# itch 页面文案与挂接清单 — T-107 / F-143 / F-150

> 用途：`game/assets/store/page.md` 是 itch.io 项目页的**唯一权威文案源**
> （中英成对）。业主/协调会话在 itch dashboard 的 Edit page → Description
> 里粘贴「二、页面正文」，媒体按「一、挂接清单」上传；`$0+可选捐赠`等
> 页面参数见「三、dashboard 参数速查」。FAQ 直接复用本目录 `faq.md`
> （T-108 已定稿，口径=设计 §1/§16+Q7+Q16A）。

## 一、媒体挂接清单（全部来自本目录，T-108 真实录屏物料）

| 位置 | 文件 | 说明 |
|---|---|---|
| Cover（封面/搜索缩略图） | `capsule-512-v020-alpha.png` | E1 石斧远古 × E6 星门星舰分屏 512×512 |
| 首屏动图（Description 第一行媒体） | `hero-dual-v020-alpha.gif` | 双拼主 GIF：①断链减速→②军令代建，11.9 s |
| 玩法小节动图① | `demo-breakchain-v020-alpha.gif` | 断链减速单段（5.5 s） |
| 玩法小节动图② | `demo-order-build-v020-alpha.gif` | 军令代建单段（5.1 s） |
| Screenshots（顺序上传） | `shot-01-combat` → `shot-02-order-card` → `shot-03-built-report` → `shot-04-menu-loop` → `shot-05-south-gate` | 5 张 640×360，全部母片抽帧 |
| 页尾视频（itch 内嵌或 YouTube 镜像） | `demo-60s-v020-alpha.mp4` | 62.6 s 完整演示，真游戏音频 |

## 二、页面正文（Description 粘贴用，中英成对）

**一句话 / Tagline（ itch「Subtitle / Short description」字段）**

- 中文：一条生产链，一座城，一个真的会说话的系统。
- EN: One supply chain, one city — and a System that actually talks back.

---

### 中文正文

**《我在游戏觉醒系统》** 是一款**生产链塔防**：塔吃弹药，弹药靠链条（采集 →
加工 → 运输 → 装填）——断链减速不停摆，经济断点看得见。你在被围攻的大陆上
醒来，脑中响起一道电子音："叮！系统绑定成功。"从石斧到星舰，你要做的只是
对系统下一道命令：**"守住东门""多囤火油矢""下一波怕什么？"**——系统查
战局、出方案卡、等你点头、合法执行。

**特性**

- **物流即防御**：弹药链条全可视；断链的塔会哑火漏敌，补线即恢复。
- **时代即关卡**：六时代战役（远古 → 星际）；时代跃迁是最大爽点，千年后
  你能亲手挖出自己当年布下的防线。
- **觉醒系统（游戏内智能体）**：离线**军令语法**开箱即用，零配置零 Key；
  自然语言对话为可选 AI 档（BYOK 自带 API Key）。系统只用**封闭动作清单**
  （19 个带 Schema 校验的合法动作），出方案、等审批、点数限量，**永不乱
  创造**；可完全关闭，全通关全成就不受影响。
- **确定性模拟核心**：同种子 + 同命令流 = 完全一致的世界；回放与存档共享
  同一条确定性管线。
- **内容即模组**：塔/敌人/波次/科技全部 JSON 数据驱动，模组管线与样板
  模组随开源仓库提供（游戏内模组入口随后续免费更新开放）。
- **视觉与演出**：五层 51 条过场视频、记忆图鉴（集齐 8 段考古记忆解锁
  隐藏篇《零的真相》）、六时代面板皮肤与加载插画、24 枚成就图标。

**操作 / Controls**

- 建栏选塔 → **左键拖矩形**松开建造；**ESC** 取消；**右键点塔**拆除；
- **空格**暂停/恢复；**1 / 2 / 3** 变速；**R** 整行 / **C** 整列快速扩建；
- **F1** 词汇表帮助面板（三层信息层：物流/因果/敌情）；
- 左侧军令面板直接打字下令（例：守住东门 / 在东门建两座木箭塔 /
  敌情怎么样 / 三分钟后叫醒我）。

**当前版本 v0.5.0-alpha（W5 预览版）**

首发两章（E1 远古 + E2 古典）可玩 + 视觉扩充全量 + 渲染三档自动适配。
后续章节（E3~E6）与游戏内模组入口以免费更新追加。

**FAQ 三条**（中英成对，定稿源=本目录 `faq.md`，T-108）：

- **军师能听懂多自由的话？** 两个档位：离线**军令语法**开箱即用不配 Key
  （"守住东门""在东门建两座木箭塔"这类军令句）；自然语言对话是可选 AI 档
  （BYOK）。无论哪档，系统只用**封闭动作清单**（19 个带 Schema 校验的合法
  动作）在规则内行事——出招、等你审批、点数限量，永不乱创造；系统可完全
  关闭，全通关全成就不受影响。
- **AI 档要花钱吗？** 游戏端不收任何钱；AI 对话档费用由玩家自担（自带
  按量计费 API Key，GLM / MiMo 模板已预置）。注意 **Coding Plan（套餐包月）
  密钥不能用于本游戏**（服务端错误码 1315）。不配 Key 军令语法档完整可玩。
- **这游戏免费吗？** 永久免费，无任何付费项（无内购/广告/DLC 收费）。
  代码 MIT 开源、资产 CC BY-NC；后续章节以免费更新追加。

---

### English copy

**Awakened System TD** is a **supply-chain tower defense**: towers eat
ammunition, ammunition rides the chain (gather → refine → haul → load). A
broken link slows your defense to a crawl — and you can see exactly where it
snapped. You wake on a besieged continent with an electronic chime in your
head: *"Ding! System bound."* From stone axes to starships, your only job is
to issue orders: **"Hold the east gate." "Stockpile fire-oil arrows."
"What's the next wave afraid of?"** — the System reads the battlefield,
files a plan card, waits for your approval, and executes by the rules.

**Features**

- **Logistics is defense**: the whole ammo chain is visible; starved towers
  go silent and leak enemies — reconnect the line and they roar back.
- **Ages as campaigns**: six eras (stone age → interstellar); era
  transitions are the headline thrill, and millennia later you can excavate
  the very wall you built.
- **The Awakened System (in-game agent)**: the offline **command grammar**
  works out of the box with zero keys; free-form natural chat is an optional
  AI tier (BYOK). The System only acts through a **closed action list**
  (19 schema-validated actions): it proposes, you approve, points are
  rationed — it never invents powers, and you can turn it off entirely.
- **Deterministic sim core**: same seed + same command stream = the exact
  same world; replays and saves share one deterministic pipeline.
- **Content is data**: towers/enemies/waves/tech are all JSON-driven; the
  mod pipeline and sample mods ship in the open-source repo (in-game mod
  browser comes in a later free update).
- **Presentation**: 51 cutscene videos across five layers, a memory archive
  (collect 8 relics to unlock the hidden 60s synthesis episode *Truth of
  Zero*), six era skins/loading art, 24 achievement icons.

**Controls**

- Pick a tower in the build bar → **left-drag a rectangle** to place;
  **ESC** cancels; **right-click a tower** to demolish;
- **Space** pause/resume; **1 / 2 / 3** game speed; **R** whole row /
  **C** whole column quick-build;
- **F1** toggles the vocabulary help panel (three info layers: logistics /
  causality / enemy intel);
- Type orders directly into the left command panel (e.g. hold the east
  gate / build two arrow towers at the east gate / enemy report / wake me
  in three minutes).

**Current build: v0.5.0-alpha (W5 preview)** — first two chapters (E1 stone
age + E2 classical) playable, full visual expansion, auto render tiers.
Later chapters (E3–E6) and the in-game mod browser ship as free updates.

**FAQ (three)** — command freedom / AI-tier cost (BYOK) / free forever
(canonical bilingual text: `faq.md`):

- **How freely can I talk to the System?** Two tiers: the offline
  **command grammar** works out of the box with no key ("hold the east
  gate", "build two arrow towers at the east gate"); free-form natural
  chat is the optional AI tier (BYOK). Either way the System only acts
  through a **closed action list** (19 schema-validated actions) — it
  proposes, you approve, points are rationed, it never invents powers,
  and you can turn it off entirely without losing content.
- **Does the AI tier cost money?** The game charges nothing; the optional
  chat tier runs on your own pay-as-you-go API key (GLM / MiMo templates
  preconfigured). Note **"Coding Plan" subscription keys cannot be used
  here** (provider error 1315). Without any key, the command grammar tier
  is fully playable.
- **Is the game free?** Free forever, no payments of any kind (no IAP, no
  ads, no paid DLC). Code is MIT open source; assets CC BY-NC; later
  chapters ship as free updates.

---

### 公共页脚（中英，粘贴在正文最末）

- **免费与捐赠 / Free & donations**：本游戏**永久免费、无任何付费项**（无
  内购/广告/付费 DLC）。代码 MIT 开源、美术音频资产 CC BY-NC。如果你喜欢，
  页面上的**自愿捐赠按钮**（可填 $0 或任意金额）是唯一的资金入口，捐多捐少
  都不影响任何内容。
- EN: **Free forever, no payments of any kind** (no IAP, no ads, no paid
  DLC). Code is MIT open source; art/audio are CC BY-NC. If you like it,
  the optional **donate button** (any amount, $0 always fine) is the only
  funding channel and changes nothing in the game.
- **AI 生成内容披露 / AI-generated content disclosure**：插画与过场视频由
  AI 生成（预生成素材，不涉及运行时调用）；逐条台账见开源仓库
  `game/assets/expand-v1/` 下各 manifest。EN: illustration/cinematic assets
  are AI-generated (pre-baked, no runtime calls); per-item manifests live in
  the open-source repo.

## 三、dashboard 参数速查（owner 操作，不进 Description）

| 设置 | 值 |
|---|---|
| Kind of project | **Downloadable game**（桌面分发口径，D-34；HTML5 槽位=Q19 引擎门控跟进） |
| Pricing | **$0（或"Name your own price"含 $0）=可选捐赠**；不设最低价 |
| Classification / 分类检索 | Game → Genres: Strategy, Tower Defense；Platform: Windows, Linux |
| Cover | `capsule-512-v020-alpha.png` |
| Uploads | **butler 通道推送**（`bash tools/release/push_itch.sh`）：windows / linux 双通道，`--userversion v0.5.0-alpha` |
| Visibility | 装配完成后 **Publish**（草稿页匿名访问 404=预期） |
| Community | 建议开 Comments（ Issues 已由 GitHub 承担技术反馈） |

## 四、上线后回填清单

1. 页面 Publish 后把公开 URL 回填：本文件头部、`README.md` 快速开始、
   `docs/index.md`（Pages landing）、REV-20261016-T107 §上线证据。
2. 复跑 `bash tools/release/push_itch.sh`（脚本尾段自动 `curl` 复核 200）。
3. 后续版本：改 `ITCH_VERSION` 重跑 build_v050 式流水线+push_itch（通道
   保持 windows/linux，itch 档案页自动按 userversion 排版）。
