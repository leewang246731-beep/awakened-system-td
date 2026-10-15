# game/assets/store/ — 商店页物料（T-108 / F-150）

真实录屏物料（R7 口径：**只用真实 L1 录屏**，无预渲染动画/CG 冒充 gameplay）。
母片：Godot 4.7.2 内建 `--write-movie`（tools/release/record/EraRecord 录制
harness——真项目源码原样编译+真 Main 场景同构驱动，60 s@60fps 含真游戏音频）；
后期：tools/release/store_materials.py（ffmpeg 抽段/双拼/角标/胶囊）。
方法学与帧级证据见 `docs/engineering/reviews/REV-20261016-T108-store.md`。

## 物料清单（v0.2.0-alpha）

| 文件 | 规格 | 内容 |
|---|---|---|
| `hero-dual-v020-alpha.gif` | 640×360，11.9 s，≤1 MB | 双拼主 GIF：①断链减速（弹尽塔哑火漏敌）→②军令代建（军令→方案卡→塔列队），两段顺序拼接，版本角标 |
| `demo-breakchain-v020-alpha.gif` | 640×360，5.5 s | 断链减速单段（波 1 接敌：有弹塔齐射/断链塔哑火对照） |
| `demo-order-build-v020-alpha.gif` | 640×360，5.1 s | 军令代建单段（面板展开→逐字下令→方案卡→确认→2 塔就位→战报） |
| `demo-60s-v020-alpha.mp4` | 640×360@30fps，62.6 s，AAC 双声道 | 完整演示：题图（E 盘 era1 banner）+主菜单循环演出+波次玩法循环+军令代建+补线恢复，全程真游戏音频 |
| `capsule-512-v020-alpha.png` | 512×512 | 胶囊图：E1 石斧远古（左）×E6 星门星舰（右）同防线分屏——expand-v1 P4 loading 批已有概念图合成，零新生成 |
| `shot-01-combat-v020-alpha.png` | 640×360 | 截图①：波次接敌，有弹塔齐射 |
| `shot-02-order-card-v020-alpha.png` | 640×360 | 截图②：军令方案卡（审批前） |
| `shot-03-built-report-v020-alpha.png` | 640×360 | 截图③：塔就位+战报气泡 |
| `shot-04-menu-loop-v020-alpha.png` | 640×360 | 截图④：主菜单循环（游戏内实拍） |
| `shot-05-south-gate-v020-alpha.png` | 640×360 | 截图⑤：南门新防线 |
| `faq.md` | — | FAQ 三条（中英，README/itch 页复用） |

## 版本角标方案

ffmpeg drawtext 右下角 `v0.2.0-alpha`（Consolas 等宽 14~17px+半透明底，全部
动图/视频/截图/胶囊统一盖章）。像素风协调性评估：640×360 缩放显示下小字号
等宽角标与游戏位图 UI 混排差异不可辨，且换版本号无需重录母片——优于引擎内
烧录位图角标（结论与依据见 REV §角标）。

## itch 页建议用法

- 首屏动图=hero-dual GIF；断链/军令单段 GIF 放「玩法」小节；
- `capsule-512` 作 itch 主封面（cover image）；
- 截图 5 张进 screenshots 栏（顺序=上表 01→05）；
- `demo-60s` MP4 可作 itch 内嵌视频或 YouTube 镜像源；
- 页面 FAQ 区直接引用 `faq.md` 三条（中英成对）。
