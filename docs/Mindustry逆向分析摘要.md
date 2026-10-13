# Mindustry 逆向分析摘要（rea + 多子代理源码深挖）

> 分析对象：[Anuken/Mindustry](https://github.com/Anuken/Mindustry) v8-BE（v160.7，2026-10-13 克隆）
> 方法：morluto/rea v6.4.0（`rea analyze` 源码仓库语义图 + `rea inspect-artifact` 官方 Mindustry.jar 产物检验）+ 6 个并行子代理对 Java 源码做全子系统深挖。
> 状态：逆向阶段完成，结论已注入《纪元防线》设计方案。

## 0. rea 工具使用记录

| 步骤 | 命令 | 结果 |
|---|---|---|
| 1 | `rea import-reference-source D:\game\Mindustry` | ❌ Windows 主机不支持（需要 Linux/macOS 的安全文件打开特性，`unsupported_host`） |
| 2 | `rea analyze D:\game\Mindustry --format json` | ✅ 完成。rea 将仓库识别为可解析应用，对 253 个源文件（annotations 下 JSON 组件修订表等）构建语义图，产出 3MB 证据 JSON（evidence_id `ev_48820d4d…`），含模块/导入/边界关系与局限性声明。输出存于 `analysis/rea-analyze-result.json` |
| 3 | `rea inspect-artifact D:\game\Mindustry.jar` | ✅ 完成。对官方 v160.7 桌面版发行物（88.6MB，sha256 `36d94941…`）做 ZIP 中央目录清单与证据链（evidence_id `ev_d96a4214…`），确认"源码 ↔ 发行物"对应关系。输出存于 `analysis/rea-jar-inspect.md` |
| 环境限制 | `rea doctor` | 原生二进制引擎（Ghidra/Hopper/IDA）未安装且本机无 WSL；Mindustry 为开源 Java 项目，源码级分析是信息量最大的正确路径，rea 的 Android/JADX 路径无 APK 可用（官方 release 仅发布 JAR） |

**rea 的证据链方法论（证据分级、局限性显式声明、coverage 统计）被沿用为本项目所有子代理报告的规范。**

## 1. 核心架构（模块/主循环/实体系统）

- 模块：core（全部玩法，~930 Java 文件）/ desktop / server / android / ios / annotations（编译期代码生成）/ tools / tests。服务端与客户端共享 core，通过 `Vars.headless` 跳过渲染（`server/src/mindustry/server/ServerLauncher.java:22-84`）。
- 主循环：`ApplicationCore` 模块栈（Logic/Control/Renderer/UI/NetServer/NetClient），权威逻辑帧 `Logic.update()` 显式编排更新顺序：池归还→物理→单位→电力图→建筑→子弹→碰撞（`core/src/mindustry/core/Logic.java:464-500`）。
- 实体系统：**编译期扁平化的组件组合**——49 个 `*Comp` 组件类 + 注解处理器生成具体类（`mindustry.gen` 不在源码树），组件组合相同的实体共享生成类；9 个 `EntityGroup` 类型化分组做无装箱批处理、四叉树空间索引、O(1) 删除、视口裁剪（`core/src/mindustry/entities/EntityGroup.java:88-99,168-178`）。
- 内容注册：**构造即注册**——`new Item("copper"){{hardness=1;}}` 一行完成定义+注册；`ContentType` 分桶 + 统一生命周期 init/postInit/loadIcon/load（`core/src/mindustry/core/ContentLoader.java:58-137`）；id 顺序即存档语义，永不重排。
- 方块/建筑：Block=静态原型（共享配置），Building=动态实例（每格状态），泛型内部类表达行为差异（`core/src/mindustry/world/Block.java:406`）。
- 存档：分区块二进制（meta/content/map/entities/…）+ 13 个版本读取器链 + 内容 ID 按名字映射防漂移 + 保存前备份回滚（`core/src/mindustry/io/SaveVersion.java:63-88`）。

## 2. 生产/电力/物流/炮塔

- **效率 0~1 统一调节器**：所有短缺（缺料/缺电）折算成"各消费者效率取 min"，短缺=线性变慢而非停机（`core/src/mindustry/entities/comp/BuildingComp.java:1951-2010`）。
- 电力：图论电网，coverage=产出/需求平滑写回每个建筑的 `power.status`；发电阶梯 60→330→1080→7800/s（×3~6/级），高级发电机需冷却且会爆炸（钍堆 `Blocks.java:2638-2647`）。
- 物流：传送带逐物品位置模拟（铜带 5 件/s→钛带 10→管道 15 的吞吐阶梯）；Unloader 用"最满→最空"比较器自动均衡。
- 炮塔：`ItemTurret` 的弹药=`ObjectMap<Item,BulletType>` 资源→弹种映射，好弹药 DPS ×2+机制特效；PowerTurret 电力即弹药，射速=电网满足率；冷却液超频 ×5（`world/blocks/defense/turrets/`）。
- 数值曲线：钻头产量 ×1.8/级但**单位面积产量恒定**；炮塔 DPS ×2/级、射程 +30/级（160→290）；研究成本 `60 + 建造成本^1.11 × 20` 幂律；单位工厂命名即曲线（additive→multiplicative→exponential→tetrative，材料 ×6.5/级）。

## 3. 单位/AI/战斗/波次

- 护甲公式：`max(damage − armor, 10%×damage)`——线性减伤+保底，天然形成速射克轻甲/重击克重甲的克制矩阵（`core/src/mindustry/entities/Damage.java:663-665`）。
- 寻路：**流场（Dijkstra 反向泛洪）**，每(队×代价型×场型)一张，独立线程 8ms/帧预算+脏标记增量刷新；**敌方建筑血量计入通行代价（×5），敌人会权衡"绕路还是拆最薄的墙"**（`core/src/mindustry/ai/Pathfinder.java:44-92`）。
- 目标选择：单位优先于建筑；`targetPriority` 优先级压制距离；空军按 `targetFlags` 降级链选择战略目标（发电机→电池→核心）。
- 波次公式：数量 `amount + ⌊(wave−begin)/spacing⌋/unitScaling` 封顶；护盾 `base + scaling×wave`（第 30 波起 +20+30×难度/波）；Boss=状态效果（血×1.5 伤×1.3）非独立类；波间隔 2 分钟随威胁收紧，首波宽限 ×2~3（`core/src/mindustry/game/SpawnGroup.java:67-78`、`game/Waves.java:258-444`）。
- 敌人组合设计：6 条兵种线×5 阶，每 8-16 波升一阶；支援型敌人（修理敌方建筑）改变防御经济。

## 4. 战役/科技树/经济

- 战役=星球扇区网格，**难度由地理邻接计算**（相邻敌方基地数量），玩家自选扩张方向（`core/src/mindustry/type/Planet.java:386-403`）。
- 双星球=双战役线：Serpulo（波次生存、资源跨关继承、失败变残骸可复夺）vs Erekir（无波次纯攻坚、迷雾、失败清档）——同一引擎、两套规则集。
- 科技树：研究货币=全星球核心库存实物资源，**允许分期付款**；三重闸门=拓扑前置+材料自动依赖+关卡绑定目标（`SectorComplete(具体关卡)` 把"想要神炮"翻译成"去打某关"）。
- 跨关卡继承"课税"制：发射只能带走核心容量 25-50% 的资源（`launchCapacityMultiplier`），源关卡真扣资源。
- 离线回合结算（每 2 分钟一回合）：离线关卡按历史产出均值增产、敌军随机入侵、被入侵关卡时间冻结（`core/src/mindustry/game/Universe.java:151-267`）。
- 防一步登天四重闸：科技拓扑、材料依赖、地图资源地理稀缺（钍只出现在 30% 关卡）、发射容量。

## 5. 渲染/联机/逻辑处理器

- 渲染：地板按 30×30 tile chunk 烘焙静态 VBO、静态建筑进 SpriteCache、tile 改动只重烘焙脏 chunk；`Draw.z` 深度分层+排序合批（`core/src/mindustry/graphics/FloorRenderer.java:18-31`）。
- 联机：服务器权威+输入审批（速度/位移钳制、建造过 `allowAction` 统一闸门）、200ms 全量快照+客户端插值、随机数种子同步（`core/src/mindustry/core/NetServer.java:754-880`）。
- 逻辑处理器 mlog：栈式 VM，每 tick 指令预算（普通 1/tick、世界处理器 40/tick）防滥用；"传感器→条件→动作"是可借鉴的子集。

## 6. 体验/新手/模组

- 无强制教程：**谓词式提示系统**（每条提示=何时出现+何时完成两个谓词，按行为触发，完成状态持久化，8 秒冷静期，平台分支文案）（`core/src/mindustry/ui/fragments/HintsFragment.java:59,169-305`）。
- 教学关=参数化（最低难度+10 波胜利+首波时间 ×3+送开局资源），非脚本。
- 建造栏按**玩家动词**分类（10 个 Category），买不起实时变灰+逐项造价清单。
- 模组三级：JSON 数据（可反向 patch 原版、可定义整颗星球）→ JS 脚本（沙箱）→ Java jar；分发走 GitHub 索引+Release 零服务器成本——十年长青的核心原因之一。
- 移动端：同一 InputHandler 基类换手势实现，修饰键实体化为按钮，放置二次确认。

## 7. 六大子系统报告的完整版本

完整报告（含全部文件:行号证据与数值表）由 6 个分析子代理产出，直接输入设计方案撰写；如需逐份归档可再导出。关键结论均已收录于上文与设计方案附录 A。
