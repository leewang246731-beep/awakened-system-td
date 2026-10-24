# REV-20261024-T166 审美遗留清账批（业主令"都处理好，不要遗留"）

- 任务：T-166「审美遗留清账批」——08 号文 v1.40：T-165 挂账三件（①军师立绘 96 档+面板版式重构 ②滚动条+SpinBox 箭头像素皮肤 ③飘字合并寿命语义）+T-165 审查判断项四件顺手清，目标零遗留。
- 基线：HEAD=f22bbe ← T-165（f5b16cc+审查修复 be4b1c3）+v1.2.1 收官（4a628e8）
- 纪律面：R5/R7（新常量随改随标 ci-allow）/R9/T2/T3/T4；CS0104；XML 中文注释；≤最小 diff；**零新生成素材**；headless 判据零改动
- 声明：本 REV 由施工代理起草，主管验收后登记。

## 一、A 件：军师立绘 96 档+面板版式重构（T-165 遗留①）

### 路选论证（任务卡两路之外的事实驱动裁决）

任务卡给两路：路A=报告区压 ~204（96+6+50+204=356 贴合 360）；路B=面板加宽（画布 640 内有余量）。**实测后两路都不对症——真正的病根在纵向与素材档位，不在横向预算**：

`--shot t166` 探针（HEAD 侧基线+改后对照，画布域矩形）实测三连病因：

1. **立绘档位常量自 T-163 起从未生效**：`_avatar` TextureRect 缺省 `ExpandMode=KeepSize`——最小尺寸=素材自然 256×256，`AdvisorPortraitPx`（36/64）被 256 覆盖（探针：avatar=(…),(256,256)）。T-165 复评"64 档上屏可辨"与实机像素事实不符（64 档立绘在战斗面从未以 64px 出现）——本 REV 勘误登记。
2. **九宫皮肤内容内距绑架面板**：`VisualExpansionDirector.ApplyEraSkin` 的 StyleBoxTexture patch 边距=纹理 1/8（1024 素材≈129px/侧），且内容边距缺省跟随 patch 边距——展开面板被撑到 **756×675**（画布 640×360，探针：panel=(8,170),(756,675)），报告行整体沉到 y460+ 画布外。
3. **底部带纵向死磕**：T-163 建造栏移底部后，StatusLabel（全宽 y256-274，z30）+BuildBar（x164-476 y282-346，z30）压住左下面板下半——HEAD 实况=展开面板仅标题+军令行可见（y~300-365 且被裁底），轮盘/报告/立绘全部被盖或画布外。

**裁决=版式重构（任务卡标题原词），四手术刀**：

| # | 改动 | 位置 | 效果（探针实证） |
|---|------|------|------|
| 1 | `ExpandMode=IgnoreSize`+`KeepAspectCentered`+`ShrinkCenter` | SystemPanel._avatar | 立绘真实 96×96（探针 avatar=(119,167),(96,96)）；**既有 eraN-advisor.png 256 素材仅缩放显示——零重生成** |
| 2 | `AdvisorPortraitPx` 64→96 | HudIconDecisions.cs | 档位常量自本次起真实生效（根治①后） |
| 3 | 信息区改立绘右侧纵排：主行=HBox[立绘96｜VBox(信任/报告/军令示例/AI 语法提示/档位提示)]；报告宽 236→**300（T-163 基线回位——T-165 压 236 的可读性降档解除）**；提示两件补 Autowrap+300 基宽（换行最小高从逐字塔收口） | SystemPanel.BuildExpandedPanel | 面板 416×237；报告区可读性**升档**（300 宽+侧列完整五件无截断——01 图特写列全部可读） |
| 4 | 皮肤盒内容内距解耦：patch 边距只管绘制分区，内容边距显式钉 6px | VisualExpansionDirector.ApplyEraSkin | 面板不再被装饰边厚度绑架（675→237 高；绘制零改动——皮肤九宫观感不变） |
| 5 | 展开面板改**居中模态位** (112,44)+`ZIndex=12`（SystemPanel 20 层内相对，有效 32>30） | SystemPanel.Layout | 底边 281 让建造栏顶 282（1px 缝）；盖过状态行；方案卡（20+30=50）与 F1 抽屉（40/41）仍在其上——下令→确认/拒绝链零回退；**气泡常驻位 (8,324) 零改动**；资源条（z21，底~122）在展开态被模态覆盖=瞬态可接受（收起即回） |

路A/路B 与本裁决的关系：路A（压报告区）在纵向病因面前无解且伤可读性；路B（加宽）同 irrelevant。本裁决吸收两路的合法内核（贴合内容带预算+画布余量利用），以实测矩形证据定案。**08 v1.40"两条路自选并 REV 论证"条款以此履行。**

### A 件验收

- 立绘 96px 真上屏：`docs/screenshots/w10-f/01-advisor-96.png`（全景+报告区特写拼一图）+探针行 `avatar=(119, 167), (96, 96)`、`portrait_loaded=True`。
- 报告区可读性不降反升：报告 300 宽（T-163 基线）单行/双行自适应无截断；信任带/军令示例/语法提示/档位提示五件侧列全显（特写列逐件可读）。
- 军师面板其余面零回退：battle-hud 既有编排回归跑通（`w10-f/raw-battle-hud-regression/` 五张全出，04-advisor-panel 取景随居中位更新为画布 (0,28)-(560,300)）；方案卡 z50 居中弹层在展开面板之上（BuildPlan 确认/拒绝可达）；气泡/收起/军令链零改动。
- 勘误披露：T-165 REV"64 档上屏"的表述与实机不符（KeepSize 根因下 64 档从未生效）——本次根治后档位常量才与像素事实一致。

## 二、B 件：滚动条+SpinBox 箭头像素皮肤（T-165 遗留②）

**UiFeel 新两族**（代码生成像素件，零二进制资产——BuildAvatarTexture 同律）：

1. `ApplyScrollBarSkin(ScrollContainer/ScrollBar, era)`：槽=暗底+`DimEdge` 时代暗变体 1px 框（ContentMargin 定 10px 槽宽）；滑块三态=时代色暗底→提亮底→加重底（ButtonTriple 同族色阶律）；上下（横杆=左右）箭头钮=9×5/5×9 像素三角三态（normal=时代色亮 20%/highlight=45%/pressed=白——与下拉箭头同语言）。四件盒+三角按（色,方向）键静态缓存。
2. `ApplySpinBoxSkin` 补 updown 像素件：**4.7.2 C# API 无 `GetUpDown()`**（XML API 全扫证实）——等效单源=theme icon `「updown」`覆写（9×11 上尖 5 行+空 1 行+下尖 5 行，时代色亮 20%）。此即任务卡"GetUpDown 系 theme override"在 4.7.2 的落地形态。

**接线面**（`ScrollContainer` 全库扫描=三处现役，F1 抽屉为普通 VBox 无滚动件——搜索证实）：

| 面 | 色 | 位置 |
|----|----|------|
| 选关面板滚动区（29 卡滚动场） | 中性青（跨时代面——StyleHeaderButton 同口径） | CampaignMenuUi.BuildUi |
| 设置页表单滚动 | e5 时代色 | SettingsPanelUi.ApplyEraSkin |
| 战报回放事件流 | 中性青（骨架面） | ReplayUi |

验收：`02-scrollbar.png`（选关+设置两面拼一图——槽/滑块/上下三角箭头像素件在屏，无系统缺省件残留）+`03-spinbox-arrows.png`（数字框上下箭头特写）。

## 三、C 件：飘字合并保剩余寿命（T-165 遗留③，一行改）

`SessionBattlefieldUi.cs` `SpawnDamageFloat` 并入点：

- 前：`_floatsLive[newest] = (merged, FloatLifeFrames, unitId, sum);`（合计重置 45 帧寿命——合并可无限续命）
- 后：`_floatsLive[newest] = merged with { Total = sum };`（**Life 字段原样保留=剩余寿命沿用，不刷新**）

注释同步（"数字累加重显+寿命刷新"→"数字累加重显"）。改动行=REV 此处所记单行（D4 record 化后为 with 表达式形态，语义同一行）。行为语义：同目标第三条命中并入最新条，合计数字重显但淡出时点不推迟——视觉"合计数字+既定淡出节奏"。

## 四、D 件：T-165 审查判断项四件顺手清（前后对照）

| # | 项 | 前 | 后 | 证据 |
|---|----|----|----|------|
| ① | era 暗边公式双份 | UiFeel.ApplyOptionSkin disabled 边与 CampaignMenuUi 锁卡描边各手抄一份 `era×0.45+0.06 族` | 收 `UiFeel.DimEdge(era)` 单源 helper，两调用方改引 | 04-fixes-ba 第 2 行：锁章节带前（HEAD w10-e 侧车帧）后（本批）**像素一致**——单源收口零视觉漂移 |
| ② | tween 双谱系 | BattleHudUi.TransitionPanelSkin 手搓 StyleBoxFlat 拷贝（缺 ContentMargin/CornerRadius 拷贝——缓存盒将来带边距即漂移）vs UiFeel.CloneFlat(private) | CloneFlat 提 internal 公用；TransitionPanelSkin 改走它（ContentMargin/CornerRadius 全量拷贝——live 盒随源盒漂移而漂移） | 04-fixes-ba 第 1 行：HUD 顶带（资源条+预告框皮肤）前后**像素一致** |
| ③ | LineEdit 皮肤盒每调用新造 | ApplyLineEditSkin 每次 new 两盒（与文件头"按色键静态缓存"声明不符——设置 8 框+程序关+军令框各造） | (normal,focus) 盒对按时代色键静态缓存 `_lineEditPairs`——声明对齐 | 04-fixes-ba 第 3 行：设置文本框带后帧皮肤完整（**同画布域非逐像素对**：前帧=HEAD 滚位 0 侧车帧、后帧=滚到 BYOK 行同域——滚位不同内容不同行，逐像素比对不适用，如实披露；单实例性=代码推理+皮肤渲染完整） |
| ④ | `_floatsLive` 四元组 | `List<(Label Label, int Life, long UnitId, int Total)>`（2→4 元生长史） | `readonly record struct DamageFloat(Label Label, int Life, long UnitId, int Total)`——命名见义；`with` 寿命衰减=栈上拷贝（**池化零每拍堆分配不变**） | 代码面（G1/G3 编译+全测承载；飘字视觉=既有 04-damage-floats 面零改动） |

## 五、门禁与验收

- run_all 六步全绿（2026-10-24 实测）：
  - G2 红线零违规：PASS（9 checks | 0 FAIL | 0 WARN）
  - G2.5 资产规格校验：PASS（files=776 rejected=0 warned=0）
  - G2.6 模组离线校验：PASS（0 FAIL，3 mods loaded）
  - G1 测试全绿：PASS（**6967 passed, 0 failed**，1096s）
  - G3 构建零警告：PASS（Release，TreatWarningsAsErrors=true，零警告）
  - G4 冒烟通过：PASS（SMOKE_OK——运行时/程序集/EraCore 链路三合一）
  - 汇总：run_all 总体 PASS（G1~G4 全 PASS）
- headless 判据零改动：本批全部为窗口车道表现层改动+皮肤覆写（headless 不实例化窗口面）；冒烟判据行逐字节未动。
- 窗口存活：无参玩家车道 16s 进程存活+日志零 Quit 行（实测 ALIVE at 16s ✓）。
- 零新生成素材：立绘=既有 256 素材缩放显示；滚动条/SpinBox 箭头/三角件=代码生成 ImageTexture。母带零入库。

## 六、截图证据（docs/screenshots/w10-f/）

| 件 | 文件 | 内容 |
|----|------|------|
| 01-advisor-96.png | A | 96 立绘面板全景（居中模态位）+报告区特写拼一图；探针行带面板/立绘/报告真矩形 |
| 02-scrollbar.png | B | 像素滚动条两面：选关（中性青）+设置（e5 时代色） |
| 03-spinbox-arrows.png | B | SpinBox 上下箭头像素件特写 |
| 04-fixes-ba.png | D①②③ | 三行 [HEAD 前\|本批后] 横对纵叠（HUD 带/锁章节带逐像素一致=①②零漂移；③同域披露见 §四） |
| raw-before/ | 对照 | HEAD=f22bbe 基线帧（battle-hud 既有编排原样跑——A 件"前"实况：立绘画布外、面板被底部带压盖） |
| raw-after/ | 全量 | t166 编排全部分镜+前帧侧车（before-full-battle/before-04b-locked-band/before-full-settings=w10-e HEAD 归档复制） |
| raw-battle-hud-regression/ | A | battle-hud 既有编排回归（五张全出+04 取景随居中位更新） |

编排新增：`--shot-plan t166`（Main.ShotHook.PumpT166——真按钮 Pressed/真滚轮/探针行，全程零伪造；battle-hud 编排的 AdvisorPanelCropRegion 随居中模态位更新取景窗）。

## 七、诚实遗留与披露

1. **A 件勘误**（§一）：T-165"64 档上屏"与实机不符——KeepSize 根因下档位常量从未生效；本次根治并登记。
2. **l2Banner 瞬态增高**：L2 降级横幅出现时面板尾行生长（~+20px，底边~301 短暂越过建造栏顶 282）——面板 z32 盖其上，收起/恢复即回；稳态（离线 L1）无此态。瞬态披露不挂账。
3. **04 第 3 行可比性**：设置文本框带前帧=HEAD 滚位 0 侧车帧同域裁剪（内容不同行）——逐像素比对不适用，如实降级为"同域+后帧渲染完整"级证据（§四③）。
4. **展开面板与阅读带的瞬态覆盖**：居中模态在展开期覆盖资源条右段/波次预告左段/状态行（收起即回）——模态语义内含，REV 明示。
5. 遗留池新条目：无（本批目标零遗留达成；08 号文登记归主管验收后）。
