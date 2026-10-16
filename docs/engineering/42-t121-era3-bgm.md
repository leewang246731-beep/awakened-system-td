# 42 · T-121 E3 BGM 扩展报告（工业音景三态，表驱动扩曲）

| 项 | 内容 |
|---|---|
| 版本 | v1.0 |
| 日期 | 2026-10-13 |
| 状态 | **已交付**（run_all G1~G4 全绿；smoke 新增 AUDIO3_OK 段判据；EraCore.Tests 音频域 177 测试全绿，全套 5874 绿） |
| 任务卡 | T-121（E3 BGM 扩展；Wave 6，D 泳道；覆盖 F-135 E3；依赖 T-092✓） |
| 验收标准 | E3 ≥3 曲三态切换淡入淡出（08-task-plan T-121 行，可听测） |
| 依据 | 设计方案 v3.2 §14（三态分层[平静/交战/Boss 淡入淡出]）；31-visual-enrichment §1.2 E3 视觉身份卡（铆铁赭红/煤黑/蒸汽灰白+桁架铁塔+桁架三角×齿轮母题；C-03 堡垒号 Boss 入场/C-12 工业码头汽笛）；26-t092-audio（T-092 三态 BGM+CrossFadeBus+合成参数表先例，其 §8 预告"E3+ 扩曲=bgm-programs.json 加行"）；CODE_STANDARDS CS-1.4/CS-2.3/CS-3.1/CS-3.2 |
| 交付物 | game/content/audio/bgm-programs.json（v2：timbre 列+era3 行）；game/src/EraCore/Audio/BgmProgram.cs（BgmTimbre 枚举+严格解析）；BgmSynth.cs（工业音色三声部：蒸汽垫底/金属打击+铆钉/重工业低音+汽笛动机）；presentation/EraTD/audio/AudioDirector.cs（era3 段验收+AUDIO3_OK）；EraCore.Tests/Audio 三文件扩展（+10 用例）；本报告 |
| 并行纪律 | 与 T-120（E3 叙事段）同窗并行：只写 audio 扩展+测试+smoke；git add 仅本表所列路径 |

---

## 1. E3 三态曲目（表驱动，零二进制入仓）

### 1.1 参数行（bgm-programs.json v2，schema `urn:eratd:content:audio:bgm-programs:2`）

| 时代 | tempo | 根音 | 和弦（音程比） | 垫底 | 鼓点 | 低音 | 帽钉/铆钉细分 | timbre |
|---|---|---|---|---|---|---|---|---|
| era1（既有） | 84 | A2 110 Hz | 小三和弦 [1.0, 1.1892, 1.4983, 2.0] | 0.34 | 0.20 | 0.24 | 2 | acoustic |
| era2（既有） | 100 | D3 146.83 Hz | 大三和弦 [1.0, 1.2599, 1.4983, 2.0] | 0.32 | 0.22 | 0.26 | 4 | acoustic |
| **era3（新）** | **112** | **F#2 92.5 Hz** | **四度叠置汽笛和弦 [1.0, 1.3348, 1.4983, 2.0]** | **0.30** | **0.26** | **0.36** | **4** | **industrial** |

v2 变更=每行新增 timbre 列（封闭集 `acoustic|industrial`）+ era3 行 + ProgramCount 2→3（行级 ci-allow: R7 防御标注随行更新，CS-7.1）。**era 参数表数据化**：E3 与 E1/E2 并列为表行——音色族/节奏/和声/增益全部来自表，非硬编码；E4+ 扩曲仍=加行+改常量，不动渲染器。时代参数链：tempo 84→100→112（机器节拍渐密）、根音下行至 92.5 Hz（铆铁赭红的暗重）、bass_gain 0.36（重工业低音权重）。112 BPM 恰使 4 小节 4/4 循环=8.571428… s=**整 189000 样**（22050 Hz），循环长度零取整误差。

### 1.2 工业音景三声部（31 号视觉身份卡 → 合成映射）

| 态 | 累积分层 | 合成实现 |
|---|---|---|
| **calm 平静** | 和声垫（三态共享基座）+ **蒸汽垫底低频脉冲+微汽笛** | ①低频脉冲=根音下八度 46.25 Hz 正弦，逐拍升余弦搏动（机器心跳）；②蒸汽嘶鸣垫=21 分量加性噪声带（370~1110 Hz，分量整数周期化+种子相位——**数学上严格 N-周期**的"噪声"）；③微汽笛=奇数栏栏首 0.9 s 远汽笛（根音×4×纯四度，双管失谐拍频），升余弦起收零点消咔哒 |
| **combat 交战** | + **金属打击节奏+铆钉动机** | ①铁板 clang=非谐泛音 [1.0, 2.756, 5.404]×根音上八度，指数衰减 0.22 s，拍 1/3；②铆钉枪=细分网格逐位噪声体（12 ms 衰减）+2.4 kHz 金属泛音"叮"，拍 2/4 每拍 4 发（种子 LCG 逐 hit） |
| **boss** | + **重工业低音+汽笛长鸣动机** | ①低音线=根音/五度逐小节交替，tanh 驱动 2.6（acoustic 为 1.8——更密的谐波=重工业重量）；②汽笛长鸣=循环首 1 整小节双管（根音+纯四度各 ±0.0035 失谐），2 栏前半以纯五度**复现句**回应（call & response=堡垒号 Boss 入场汽笛，C-03 对位） |

峰值归一化 0.72 与 E1/E2 同响度（三态/三代等功率淡入淡出不抽 pumps）。实测（22050 Hz/4 小节）：high(combat)/high(calm)=**2.10**（铆钉高频身份，阈 1.5）、low(boss)/low(combat)=**1.21**（重工业低音，阈 1.15）、三态字节级互异、半环能量比 0.92~0.98（无缝前提，阈 [0.75,1.35]）、确定性二次渲染全等。

### 1.3 确定性机要（CS-1.4 延续）

一切持续音调分量按整数周期/循环量化（f=k/N）；蒸汽"噪声"=加性合成的整数周期分量集（相位=分量序号种子纯函数，禁共享 Random）；打击/汽笛事件=拍网格偏移的纯函数+逐 hit 种子 LCG，逐小节网格逐循环重复 ⇒ 渲染样本序列严格 N-周期（无缝循环的数学前提）。守卫三处前置（先于渲染暴露参数行违规）：铆钉/铁板网格尾端、微汽笛尾端、长鸣尾端各一（BgmSynthTests 三条非法用例分别触发）。**E1/E2 声学路径逐字节不变**（重构仅抽取 RenderPad/RenderBass，表达式与求值序不变；提交前以 git HEAD 旧源码并排渲染 6 循环 cmp 全等实证）。

## 2. 三态切换（CrossFadeBus 复用，era3 实测等功率迁移）

淡入淡出状态机/等功率包络（from²+to²≡1）/重定向主导侧语义全部复用 T-092 CrossFadeBus（era 无关的纯逻辑）。era3 段验收（AudioDirector.RunEra3Acceptance，headless）：三态迁移×3+等功率中点 √½+完成态；**live 实测**——SetActiveEra("era3") 后经真总线驱动中点，era3 Combat 播放器 VolumeDb=主音量 0.8×√½≈−4.94 dB（±0.05 dB），即工业音色的三态切换在真实播放器增益上走过等功率中点。现场态驱动链（敌>0→交战/Boss 波→Boss）与 AudioDirector._Process 既有逻辑共用，EraTD 关卡侧 era 路由随 E3 关卡工作接线（SetActiveEra 公共接口就位，同 T-092 §8 裁定）。

## 3. 音画对位注记（E3 音景 ↔ 31 号视觉身份卡）

- **汽笛动机=桁架母题的音画对位**：视觉母题=桁架三角里嵌齿轮（三角骨架+旋转件）；音景对位=四度叠置汽笛和弦（根-纯四-纯五-八度：三个开放音程=三角三边）+长鸣/复现的双句呼吸（齿轮的转动往复）。Boss 长鸣小节=整小节持续（钢铁堡垒的庞大），复现句上纯五度（汽笛应答的码头纵深）——C-03"Boss 入场·堡垒号 钢铁堡垒驶入，烟囱喷吐"的听觉同位素。
- **色板的听觉映射**：铆铁赭红=92.5 Hz 暗根音+铁板 clang 的中频氧化质感；煤黑=低频脉冲的持续暗底；蒸汽灰白=嘶鸣垫的白噪带+微汽笛的远场淡入——三色各有一席声音。
- **母题重复手法（31 号 §1.2 母题几何链）**：玩家从 E3 起会主动预测下一形态——音景同构：E1/E2 的"垫底→鼓点→低音"累积分层骨架原样保留，E3 只换音色族（timbre 列），跨代听感连续而质感跃迁（石木→青铜→铆铁的材质听觉史）。
- **节奏=机器**：112 BPM 逐拍搏动低频脉冲与铆钉细分网格，对位传送带双层滚动（T-119 F-127）与齿轮动画的机械节律。

## 4. 冒烟扩展（smoke 音频段判据）

AudioDirector 既有 AUDIO_OK 行随表自动更新（eras=3 loops=9 files=9）；新增 era3 段硬门禁（失败 Quit(1)）：

```
AUDIO3_OK bgm=3states era=e3 loop=seamless timbre=industrial transitions=3 equal_power=mid
         loops=3 distinct=3 halves=balanced loop_end=pinned whistle=long+reprise+faint2
         clangs=8 rivets=32 fade=3s player_gain=mid-db eras=3 files=9 source=procedural
```

断言链：era3 表行+timbre 列；三态字节级互异+与 era1 相异；loop=seamless（LoopEnd 钉死样本数+半环能量均衡）；等功率三迁移+中点 √½；era3 播放器中点增益实测 −4.94 dB；9 WAV 落盘。可听测产物：res://audio/generated/bgm-era3-{calm,combat,boss}.wav（gitignore 排除，任意播放器人工验听）。

## 5. 测试清单（EraCore.Tests/Audio，177 项=T-092 既有 167+新增 10）

| 文件 | T-121 变更 |
|---|---|
| BgmProgramTests | 三行合法解析（era3 timbre/派生时序/时代递进）；timbre 封闭集边界 Theory；timbre 未知/缺列非法 Theory；冻结行数 3（插第 4 行拒绝） |
| BgmSynthTests | era3 夹具（shipped 形态 4 小节）；频带能量态分离（high 2.10×/low 1.21×）；三态互异；era 相异（+era3 vs era1）；半环均衡；确定性（含种子噪声）；工业网格三守卫非法用例（铆钉/微汽笛/长鸣 cramped 各一） |
| AudioContentTests | 入库表=3 行九循环端到端；era3=Industrial、era1/2=Acoustic（表驱动断言） |
| CrossFadeBusTests/WavContainerTests 等 | 零变更（era 无关逻辑复用；回归随全套绿） |

## 6. 决策与遗留

- **timbre 列=命名音色族而非逐参数音色对象**：T-092 先例=音乐事实（节奏/和声/增益）入表、乐器物理（KickFromHz 等）为代码常量；timbre 族选择是每时代一行的事实，乐器物理延续代码常量——避免 schema 膨胀为逐分量参数表。外包真人作曲替换线（26 号 §1 预留）不受影响。
- **蒸汽噪声=加性合成而非滤波噪声**：滤波器状态在循环边界不可周期化；加性整数周期分量集是唯一既"像噪声"又严格 N-周期的路线（确定性机要优先，CS-1.4）。
- **bass_gain 0.28→0.36 定稿**：0.28 时 low(boss)/low(combat) 实测 1.127<1.15 阈（交战层铁板中频摊薄低频占比）；0.36 实测 1.21——"重工业低音"的表参数即听感权重。
- **E3 关卡侧 BGM 路由**：SetActiveEra("era3") 接口就位；EraTD 关卡→时代路由随 E3 关卡宿主工作落（同 26 号 §8 Boss 波接线裁定，非本卡范围）。
- **军师语音/旁白 era3 台词锚**：narration.json 仍为 E1/E2 四锚——E3 开场白/胜利交底锚随 T-120 叙事章定稿后按 26 号 §4 同规格补行（音频域接口零变更）。
