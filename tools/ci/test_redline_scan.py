#!/usr/bin/env python3
"""
test_redline_scan.py — redline_scan.py 自测（任务 T-027；零三方依赖，
`python3 test_redline_scan.py` 直跑）。

用例（正反样例 + 豁免 + CLI 端到端）：
  1. clean_tree_passes        正样例：干净仓库 9 项检查全 PASS、退出码 0
  2. dirty_tree_caught        反样例：故意违规的临时仓库逐条被抓到
                              （检查 ID/文件/行号一一对应，无漏报无多报）
  3. exemptions_work          ci-allow 行级豁免 + allowlist 路径/节级豁免生效
  4. cli_exit_codes_and_json  命令行入口退出码与 JSON 报告结构
  5. t8_golden_independence   T8 金样独立 commit：(commit, files) 判定表驱动
                              + 真实 git 仓库端到端（金样+产品同 commit 抓到、
                              金样+Tests-only 干净、allowlist 首版豁免生效）
  6. r7_numeric_anchors       R7 数值锚点：锚点提取（跳元约定键/注解键/白名单）
                              + 产品代码命中抓到 + Balance ≥10 魔数（R1 覆盖）
                              + 字符串内容不误报 + Tests 不扫 + 行级豁免
  7. t3_class_header_window  T3 类级标签窗口（W3 收官 F4 假阳回归）：标签在
                              >3 行注释块头部+其下隔 [Collection]/[Trait]
                              属性行的类头必须判已关联（Legacy
                              EraTransitionTests/RelicMapperTests、
                              OffSystemRegressionTests 同形态曾 33 条假 WARN）；
                              嵌套 helper 类不遮蔽外围测试类类级标签
                              （IHttpPortContractTests.FakeHandler 形态）；
                              同形态但无标签的对照类必须仍然 WARN

样例目录均在系统临时目录中创建、用后删除；git 检查用临时仓库的真实
HEAD 作为基线（--baseline 覆盖），不伪造任何运行结果。
"""

from __future__ import annotations

import importlib.util
import json
import os
import shutil
import subprocess
import sys
import tempfile

sys.dont_write_bytecode = True  # 不在 tools/ci 留下 __pycache__

HERE = os.path.dirname(os.path.abspath(__file__))

_spec = importlib.util.spec_from_file_location(
    "redline_scan", os.path.join(HERE, "redline_scan.py"))
rs = importlib.util.module_from_spec(_spec)
sys.modules["redline_scan"] = rs  # dataclass 装饰器需要模块已在 sys.modules 注册
_spec.loader.exec_module(rs)

PASS = 0
FAIL = 0


def check(cond: bool, label: str) -> None:
    global PASS, FAIL
    if cond:
        PASS += 1
        print(f"  ok   {label}")
    else:
        FAIL += 1
        print(f"  FAIL {label}")


def w(root: str, rel: str, content: str) -> str:
    path = os.path.join(root, rel.replace("/", os.sep))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(content)
    return path


def git(root: str, *args: str) -> str:
    r = subprocess.run(
        ["git", "-C", root, "-c", "user.email=selftest@local",
         "-c", "user.name=selftest", *args],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=60)
    if r.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)}: {r.stderr}")
    return r.stdout


def init_repo(root: str) -> str:
    git(root, "init", "-q")
    git(root, "add", "-A")
    git(root, "commit", "-q", "-m", "selftest fixture")
    return git(root, "rev-parse", "HEAD").strip()


# ---------------------------------------------------------------- 样例仓库

CLEAN_CALC_CS = """namespace Clean.Core;

public static class Calc
{
    public static int Add(int a, int b) => a + b;
}
"""

CLEAN_ISTORE_CS = """namespace Clean.Core.IO;

public interface IStore
{
    string Root { get; }
}
"""

CLEAN_MAIN_CS = """using Godot;

namespace CleanTD;

public partial class Main : Node
{
    public override void _Ready()
    {
        GD.Print("SMOKE_OK");
        GetTree().Quit(0);
    }
}
"""

CLEAN_CALC_TESTS_CS = """using Clean.Core;
using Xunit;

namespace Clean.Tests;

public class CalcTests
{
    [Theory] // legal
    [InlineData(1, 2, 3)]
    public void Add_sums_two_values(int a, int b, int expected)
    {
        // [F-999] 自测样例关联注释
        Assert.Equal(expected, Calc.Add(a, b));
    }

    [Theory] // boundary
    [InlineData(0, 0, 0)]
    public void Add_handles_zero_boundary(int a, int b, int expected)
    {
        // [F-999] 自测样例关联注释
        Assert.Equal(expected, Calc.Add(a, b));
    }

    [Fact] // illegal input
    public void Add_rejects_nothing_but_is_pure()
    {
        // [C-99] 自测样例关联注释
        Assert.Equal(-3, Calc.Add(-1, -2));
    }
}

// [C-01] 类级关联样例（自测：类声明处标注，方法体内不再重复）
public class CalcClassTagTests
{
    [Fact]
    public void Add_is_commutative()
    {
        Assert.Equal(Calc.Add(2, 3), Calc.Add(3, 2));
    }
}
"""

CLEAN_ALLOWLIST = """# 自测样例 allowlist
T2: game/src/EraCore/IO/IStore.cs | 纯端口接口（自测样例）
"""

BAD_CS = """namespace Dirty.Core;

public static class Bad
{
    // TODO: 修复这个占位实现
    public static string Path() => @"C:\\Users\\dev\\secrets.txt";
}
"""

ORPHAN_CS = """namespace Dirty.Core;

public static class Orphan
{
    public static int Twice(int x) => x * 2;
}
"""

BAD_TESTS_CS = """using Xunit;

namespace Dirty.Tests;

public class BadTests
{
    [Fact]
    public void Tautology_true()
    {
        Assert.True(true);
    }

    [Fact]
    public void Tautology_equal_same_expression()
    {
        var v = 1;
        Assert.Equal(v, v);
    }

    [Fact]
    public void Empty_catch_swallows()
    {
        try { _ = int.Parse("1"); } catch { }
        Assert.True(1 == 1);
    }

    [Fact]
    public void No_assertion_at_all()
    {
        var s = new object();
        _ = s.ToString();
    }

    [Fact]
    public void Real_assert_without_fid()
    {
        Assert.Equal(2, 1 + 1);
    }

    [Fact]
    public void Sk_key_literal()
    {
        var k = "sk-abcdefghijklmnopqrstuv";
        Assert.Equal(25, k.Length);
    }

    [Fact]
    public void Api_key_literal()
    {
        const string apiKey = "supersecretvalue1";
        Assert.Contains("super", apiKey);
    }
}
"""

DOCS_SECTIONS_MD = """# 样例文档

环境在别处提及 D:\\game\\tools\\payload —— 节外路径，应被抓到。

## 三（附）、环境事实登记

| 项 | 值 |
|---|---|
| 工具 | D:\\tools\\registered\\bin |

## 四、其他

节外内容。
"""

LEGACY_CS = """namespace Legacy;

public static class Old
{
    public static string Root() => @"D:\\legacy\\old\\root";
}
"""

EXEMPT_MARKER_CS = """namespace Exempt;

public static class Registered
{
    // ci-allow: R9 T-027 D-03 登记的开发机根（上一行豁免样例）
    public const string DevRoot = @"D:\\game\\game\\data";
    public const string Other = @"C:\\temp\\scratch"; // ci-allow: R9 T-027 行内豁免样例
    public const string NotExempt = @"C:\\temp\\nope";
}
"""


def build_clean_root(t: str) -> str:
    w(t, "game/Directory.Build.props", "<Project />\n")
    w(t, "game/src/EraCore/Calc.cs", CLEAN_CALC_CS)
    w(t, "game/src/EraCore/IO/IStore.cs", CLEAN_ISTORE_CS)
    w(t, "game/src/EraCore.Tests/CalcTests.cs", CLEAN_CALC_TESTS_CS)
    w(t, "game/presentation/EraTD/scripts/Main.cs", CLEAN_MAIN_CS)
    w(t, "tools/ci/allowlist.txt", CLEAN_ALLOWLIST)
    return t


def build_dirty_root(t: str) -> str:
    w(t, "game/src/EraCore/Bad.cs", BAD_CS)
    w(t, "game/src/EraCore/Orphan.cs", ORPHAN_CS)
    w(t, "game/src/EraCore.Tests/BadTests.cs", BAD_TESTS_CS)
    w(t, "docs/engineering/sections.md", DOCS_SECTIONS_MD)
    w(t, "game/legacy/Old.cs", LEGACY_CS)
    w(t, "tools/ci/allowlist.txt",
      "game/legacy | 自测：旧目录整目录豁免\n")
    return t


# ---------------------------------------------------------------- 用例 1

def test_clean_tree_passes() -> None:
    print("[1] 正样例：干净仓库必须全过")
    t = tempfile.mkdtemp(prefix="redline_clean_")
    try:
        build_clean_root(t)
        baseline = init_repo(t)
        report = rs.run_scan(t, baseline=baseline)
        by_id = {c["id"]: c for c in report.checks}
        check(len(report.checks) == 9, f"运行全部 9 项检查（实际 {len(report.checks)}）")
        check(all(c["status"] == "PASS" for c in report.checks),
              "干净样例 9 项全部 PASS：" +
              ", ".join(f"{c['id']}={c['status']}" for c in report.checks))
        check(report.exit_code == 0, "退出码 0")
        fails = [f for f in report.findings if f.severity == "FAIL"]
        check(not fails, f"无 FAIL 发现项（实际 {[f.check for f in fails]}）")
        t3 = by_id.get("T3", {})
        check(t3.get("warn") == 0,
              "T3 覆盖率 100%（4/4：3 个方法级 + 1 个类级关联）")
    finally:
        shutil.rmtree(t, ignore_errors=True)


# ---------------------------------------------------------------- 用例 2

def test_dirty_tree_caught() -> None:
    print("[2] 反样例：故意违规必须逐条被抓到")
    t = tempfile.mkdtemp(prefix="redline_dirty_")
    try:
        build_dirty_root(t)
        baseline = init_repo(t)
        bogus = "0" * 40
        report = rs.run_scan(t, baseline=bogus)
        got = {(f.check, f.path, f.line) for f in report.findings}

        expected = {
            # —— R9 绝对路径（URL/res:// 不误报由用例 1 的 Main.cs 与 props 保证）
            ("R9", "game/src/EraCore/Bad.cs", 6),          # C:\Users\dev\secrets.txt
            ("R9", "docs/engineering/sections.md", 3),     # 节外 D:\game\tools\payload
            ("R9", "docs/engineering/sections.md", 9),     # 节内路径（本用例未配置节豁免）
            # —— T1 残留标记（仅注释行）
            ("T1", "game/src/EraCore/Bad.cs", 5),          # // TODO
            # —— T2 src↔test 映射缺失（Bad 有 BadTests；Orphan 没有）
            ("T2", "game/src/EraCore/Orphan.cs", 1),
            # —— T4 恒真/空断言（行号=违规行；无断言方法=属性行）
            ("T4", "game/src/EraCore.Tests/BadTests.cs", 10),   # Assert.True(true)
            ("T4", "game/src/EraCore.Tests/BadTests.cs", 17),   # Assert.Equal(v, v)
            ("T4", "game/src/EraCore.Tests/BadTests.cs", 23),   # 空 catch
            ("T4", "game/src/EraCore.Tests/BadTests.cs", 24),   # Assert.True(1 == 1)
            ("T4", "game/src/EraCore.Tests/BadTests.cs", 27),   # 无断言测试方法
            # —— R5 密钥
            ("R5", "game/src/EraCore.Tests/BadTests.cs", 43),   # sk-…（25 字符）
            ("R5", "game/src/EraCore.Tests/BadTests.cs", 50),   # apiKey 非空字面量
            # —— R13 基线不可达
            ("R13", "-", 0),
            # —— T3 缺关联（WARNING；7 个方法全部缺 [F-xxx] 注释，行号=属性行）
            ("T3", "game/src/EraCore.Tests/BadTests.cs", 7),
            ("T3", "game/src/EraCore.Tests/BadTests.cs", 13),
            ("T3", "game/src/EraCore.Tests/BadTests.cs", 20),
            ("T3", "game/src/EraCore.Tests/BadTests.cs", 27),
            ("T3", "game/src/EraCore.Tests/BadTests.cs", 34),
            ("T3", "game/src/EraCore.Tests/BadTests.cs", 40),
            ("T3", "game/src/EraCore.Tests/BadTests.cs", 47),
        }
        missing = expected - got
        unexpected = {g for g in got if g not in expected}
        check(not missing, f"预期违规全部被抓到（漏报 {sorted(missing)}）")
        check(not unexpected, f"无计划外发现项（多报 {sorted(unexpected)}）")
        check(report.exit_code == 1, "存在 FAIL 时退出码 1")
        # T4 中 No_assertion_at_all 同时被 T4(无断言) 与 T3(缺 F-ID) 命中——跨检查不互斥
        check(any(f.check == "R5" and "sk-" in f.message for f in report.findings),
              "R5 消息包含命中片段（可读性）")
    finally:
        shutil.rmtree(t, ignore_errors=True)


# ---------------------------------------------------------------- 用例 3

def test_exemptions_work() -> None:
    print("[3] 豁免机制：行级 ci-allow / allowlist 路径与节级")
    t = tempfile.mkdtemp(prefix="redline_exempt_")
    try:
        build_clean_root(t)
        w(t, "game/src/EraCore/Registered.cs", EXEMPT_MARKER_CS)
        w(t, "game/legacy/Old.cs", LEGACY_CS)
        w(t, "docs/engineering/sections.md", DOCS_SECTIONS_MD)
        w(t, "tools/ci/allowlist.txt",
          "T2: game/src/EraCore/IO/IStore.cs | 纯端口接口（自测样例）\n"
          "game/legacy | 自测：旧目录整目录豁免\n"
          "docs/engineering/sections.md :: 三（附）、环境事实登记 | 自测：登记节豁免\n")
        baseline = init_repo(t)
        report = rs.run_scan(t, baseline=baseline)
        r9 = [f for f in report.findings if f.check == "R9"]

        # Registered.cs：上一行豁免（DevRoot）与行内豁免（Other）生效，
        # 未标记者（NotExempt，第 8 行）必须被抓到
        lines = {f.line for f in r9 if f.path == "game/src/EraCore/Registered.cs"}
        check(lines == {8}, f"行级 ci-allow 生效：仅未标记行被抓到（实际 {lines}）")
        # game/legacy 整目录豁免
        check(not any(f.path.startswith("game/legacy") for f in r9),
              "allowlist 目录前缀豁免生效（game/legacy）")
        # docs 节级豁免：节内 D:\tools\registered 不报，节外 D:\game\tools\payload 报
        doc_lines = {f.line for f in r9 if f.path == "docs/engineering/sections.md"}
        check(doc_lines == {3}, f"节级豁免生效：仅节外路径被抓到（实际 {doc_lines}）")
        # Registered.cs 无测试文件但豁免只登记了 R9——T2 必须仍然报
        check(any(f.check == "T2" and f.path.endswith("Registered.cs")
                  for f in report.findings),
              "豁免作用域受限：未声明 T2 的文件仍被 T2 抓到")
        check(report.exit_code == 1, "存在未豁免违规时退出码 1")
    finally:
        shutil.rmtree(t, ignore_errors=True)


# ---------------------------------------------------------------- 用例 4

def test_cli_exit_codes_and_json() -> None:
    print("[4] CLI 端到端：退出码与 JSON 报告")
    scanner = os.path.join(HERE, "redline_scan.py")
    t = tempfile.mkdtemp(prefix="redline_cli_")
    try:
        build_clean_root(t)
        baseline = init_repo(t)
        json_path = os.path.join(t, "report.json")
        r = subprocess.run(
            [sys.executable, scanner, "--root", t, "--baseline", baseline,
             "--json", json_path],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)
        check(r.returncode == 0, f"干净仓库 CLI 退出码 0（实际 {r.returncode}）")
        with open(json_path, "r", encoding="utf-8") as fh:
            data = json.load(fh)
        check(data["exit_code"] == 0 and data["counts"]["fail_findings"] == 0,
              "JSON 报告结构与 exit_code 字段正确")
        check(len(data["checks"]) == 9 and all(
            c["status"] == "PASS" for c in data["checks"]),
            "JSON 检查表 9 项全 PASS")

        build_dirty_root(t)
        baseline2 = init_repo(t)
        r2 = subprocess.run(
            [sys.executable, scanner, "--root", t, "--baseline", "0" * 40],
            capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)
        check(r2.returncode == 1, f"违规仓库 CLI 退出码 1（实际 {r2.returncode}）")
        check("R13" in r2.stdout and "R9" in r2.stdout,
              "人读报告包含 R9/R13 检查行")
    finally:
        shutil.rmtree(t, ignore_errors=True)


# ---------------------------------------------------------------- 用例 5（T8）

def test_t8_predicate_table() -> None:
    print("[5] T8 金样独立 commit：(commit, files) 判定表驱动")
    cases = [
        # (files, 期望 violates)
        (["game/content/golden/a.json"], False),                       # 仅金样
        (["game/src/EraCore/Sim/Sim.cs"], False),                      # 仅产品
        (["game/content/golden/a.json",
          "game/src/EraCore/Sim/Sim.cs"], True),                       # 金样+产品 → FAIL
        (["game/content/golden/a.json",
          "game/src/EraCore.Tests/SimTests.cs"], False),               # Tests 不算产品
        (["game/content/golden/a.json", "game/src/EraCore.Tests/SimTests.cs",
          "game/src/EraCore/Sim/Sim.cs"], True),                       # 混合，产品在场 → FAIL
        (["game/content/golden/readme.txt",
          "game/src/EraCore/Sim/Sim.cs"], False),                      # 非 .json 金样文件
        (["game/content/golden2/a.json",
          "game/src/EraCore/Sim/Sim.cs"], False),                      # 目录前缀不同
        ([], False),
    ]
    for files, want in cases:
        got = rs.t8_commit_violates(files)
        check(got == want, f"t8_commit_violates({files[0] if files else '[]'}, …{len(files)}) == {want}")


def test_t8_end_to_end() -> None:
    print("[5b] T8 端到端：真实 git 提交（金样+产品被抓 / 金样+Tests 干净 / 首版豁免）")
    t = tempfile.mkdtemp(prefix="redline_t8_")
    try:
        build_clean_root(t)
        baseline = init_repo(t)

        # commit 2：金样 + EraCore 产品代码 → 违规（首版未豁免形态）
        w(t, "game/content/golden/wave-golden.json",
          '{"content_type": "Golden", "items": [{"wave": 1}]}\n')
        w(t, "game/src/EraCore/Extra.cs",
          "namespace EraCore;\n\npublic static class Extra\n{\n"
          "    public const int A = 1;\n}\n")
        git(t, "add", "-A")
        git(t, "commit", "-q", "-m", "golden + engine product code")
        sha2 = git(t, "rev-parse", "--short", "HEAD").strip()

        # commit 3：金样 + 仅测试代码 → 干净
        w(t, "game/content/golden/other-golden.json",
          '{"content_type": "Golden", "items": [{"wave": 2}]}\n')
        w(t, "game/src/EraCore.Tests/ExtraTests.cs",
          "using Xunit;\n\nnamespace EraCore.Tests;\n\n"
          "public class ExtraTests\n{\n"
          "    [Fact]\n"
          "    public void A_is_one()\n"
          "    {\n"
          "        // [F-999] 自测样例\n"
          "        Assert.Equal(1, Extra.A);\n"
          "    }\n"
          "}\n")
        git(t, "add", "-A")
        git(t, "commit", "-q", "-m", "golden + tests only")

        report = rs.run_scan(t, baseline=baseline)
        t8 = [f for f in report.findings if f.check == "T8"]
        check(len(t8) == 1 and t8[0].path == sha2,
              f"金样+产品 commit 被抓且仅此一个（实际 {[(f.path, f.line) for f in t8]}）")
        check("sim" in t8[0].message.lower() or "产品" in t8[0].message,
              "T8 消息提示产品侧文件与豁免通道")
        check(report.exit_code == 1, "T8 违规时退出码 1")

        # allowlist 首版豁免（短 sha 条目）→ T8 归零
        w(t, "tools/ci/allowlist.txt",
          "T2: game/src/EraCore/IO/IStore.cs | 纯端口接口（自测样例）\n"
          f"T8: {sha2} | 自测首版豁免样例\n")
        git(t, "add", "-A")
        git(t, "commit", "-q", "-m", "register first-creation exemption")
        report2 = rs.run_scan(t, baseline=baseline)
        t8b = [f for f in report2.findings if f.check == "T8"]
        check(not t8b, f"allowlist `T8: <sha>` 豁免生效（实际 {t8b}）")
        by_id = {c["id"]: c for c in report2.checks}
        check(by_id["T8"]["status"] == "PASS" and "1 首版豁免" in by_id["T8"]["note"],
              f"T8 计数注记含首版豁免（实际 {by_id['T8']['note']}）")
    finally:
        shutil.rmtree(t, ignore_errors=True)


# ---------------------------------------------------------------- 用例 6（R7）

T8_GOLDEN_JSON = '{"content_type": "Golden", "items": [{"wave": 1}]}\n'

ERA1_TOWERS_JSON = """{
  "content_type": "Building",
  "tick_rate_hz": 30,
  "cell_size_px": 32,
  "ammo_convention": { "shots_per_ammo_item": 10, "_source": "v3.2" },
  "_annotation": { "hp": 1234 },
  "items": [
    { "id": "era1:tower-x", "hp": 2500, "damage_per_shot": 77,
      "cost_total": 15, "range_cells": 5.5 }
  ]
}
"""

ANCHORED_CS = """namespace EraCore.Combat;

public static class Anchored
{
    public const int BadAnchor = 77;
    public const int TickRate = 30;
    public const int ShotsPerItem = 10;
    public const int NotAnAnchor = 999;
    public const int AnnotationKey = 1234;
    // ci-allow: R7 T-999 自测：行级豁免样例
    public const int ExemptAnchor = 2500;
    public static string Note() => "hp 2500 出现在字符串里";
}
"""

BALANCE_RULE_CS = """namespace EraCore.Balance;

public static class Rule
{
    public const int Magic = 12;
    public const int Small = 9;
    public const int Tick = 30;
    public const double Structural = 0.3;
}
"""

ANCHORED_TESTVALUES_CS = """namespace EraCore.Tests;

public static class AnchoredTestValues
{
    public const int Ghost = 2500;
}
"""


def test_r7_numeric_anchors() -> None:
    print("[6] R7 数值锚点：提取口径 + 命中抓取 + R1 覆盖 + 豁免")
    t = tempfile.mkdtemp(prefix="redline_r7_")
    try:
        build_clean_root(t)
        w(t, "game/content/era1/towers.json", ERA1_TOWERS_JSON)
        w(t, "game/src/EraCore/Combat/Anchored.cs", ANCHORED_CS)
        w(t, "game/src/EraCore/Balance/Rule.cs", BALANCE_RULE_CS)
        w(t, "game/src/EraCore.Tests/AnchoredTestValues.cs", ANCHORED_TESTVALUES_CS)
        baseline = init_repo(t)
        report = rs.run_scan(t, baseline=baseline)
        r7 = {(f.path, f.line) for f in report.findings if f.check == "R7"}

        expected = {
            ("game/src/EraCore/Combat/Anchored.cs", 5),   # 77 命中 damage_per_shot 锚点
            ("game/src/EraCore/Balance/Rule.cs", 5),      # 12：Balance ≥10 魔数（R1 覆盖）
        }
        check(r7 == expected,
              f"R7 恰好命中锚点行与 Balance 魔数行（实际 {sorted(r7)}）")
        by_id = {c["id"]: c for c in report.checks}
        check(by_id["R7"]["note"].startswith("3 锚点"),
              f"锚点集=3（2500/77/15；10/30 白名单、32/1234 跳过）实际注记 {by_id['R7']['note']}")
        # 白名单/跳过键/注解键/字符串内容/Tests 目录均不报：
        #   TickRate=30、ShotsPerItem=10（域常量白名单）；NotAnAnchor=999（非锚点且非 Balance）；
        #   AnnotationKey=1234（'_' 注解键不入锚点集）；ExemptAnchor=2500（上一行 ci-allow）；
        #   Note() 字符串里的 2500（字符串内容抹除）；AnchoredTestValues=2500（Tests 不扫）
        check(report.exit_code == 1, "R7 存在 FAIL 时退出码 1")

        # 豁免后归零（同一违规行加 ci-allow 行内尾注形态）
        w(t, "game/src/EraCore/Balance/Rule.cs", BALANCE_RULE_CS.replace(
            "public const int Magic = 12;",
            "public const int Magic = 12; // ci-allow: R7 T-999 自测豁免"))
        git(t, "add", "-A")
        git(t, "commit", "-q", "-m", "exempt balance magic")
        report2 = rs.run_scan(t, baseline=baseline)
        r7b = [f for f in report2.findings if f.check == "R7"]
        check(len(r7b) == 1, f"仅剩 Combat 锚点行（Balance 行内尾注豁免生效；实际 {r7b}）")
    finally:
        shutil.rmtree(t, ignore_errors=True)


# ---------------------------------------------------------------- 用例 7（T3 类头窗）

# 类级标签在 >3 行注释块头部、其下再隔 [Collection]/[Trait] 属性行——
# 固定 3 行窗曾整块漏扫（W3 收官 F4：Legacy EraTransitionTests /
# RelicMapperTests / OffSystemRegressionTests 同形态共 33 条假 WARN）。
# 第二类=无标签对照（必须仍 WARN）；第三类=嵌套 helper 抢"最近声明"
# （外围测试类类级标签必须覆盖，IHttpPortContractTests.FakeHandler 形态）。
T3_ATTR_WINDOW_TESTS_CS = """using Xunit;

namespace Clean.Tests;

// [C-20][F-056] 自测样例：类级标签位于超过 3 行的注释块头部（第 1 行），
// 其下再隔 [Collection]/[Trait] 属性行——固定 3 行窗曾整块漏扫，
// 类头窗必须跳过属性行延伸至首个非注释非属性行（F4 假阳回归）。
[Collection("SelftestSerial")]
[Trait("Feature", "F-056")]
public class AttrWindowTaggedTests
{
    [Fact]
    public void Tagged_via_class_comment_above_attributes()
    {
        Assert.Equal(2, 1 + 1);
    }
}

// 无标签对照：同形态（长注释块+属性行）但块内无任何 F-ID/C-ID
// ——窗口放宽不得放过真缺口，该类方法必须仍然 WARN。
[Collection("SelftestSerial")]
public class AttrWindowUntaggedTests
{
    [Fact]
    public void Untagged_counterpart_still_warns()
    {
        Assert.Equal(3, 1 + 2);
    }
}

// 嵌套 helper 形态：外围测试类带类级标签，方法前声明的嵌套 private
// class 不带——外围类级标签必须覆盖其后方法（最近声明=helper 不遮蔽）。
// [C-16][F-162]
public class NestedHelperOwnerTaggedTests
{
    private sealed class FakeHandler
    {
        public int Nop(int x) => x;
    }

    [Fact]
    public void Tagged_via_outer_class_with_nested_helper()
    {
        Assert.Equal(4, 2 + 2);
    }
}
"""


def test_t3_class_header_window() -> None:
    print("[7] T3 类级标签窗口：属性行不切断 + 嵌套 helper 不遮蔽（F4 假阳回归）")
    t = tempfile.mkdtemp(prefix="redline_t3win_")
    try:
        build_clean_root(t)
        w(t, "game/src/EraCore.Tests/AttrWindowTests.cs", T3_ATTR_WINDOW_TESTS_CS)
        baseline = init_repo(t)
        report = rs.run_scan(t, baseline=baseline)

        # ① 仅无标签对照类的方法报 WARN（行号=其 [Fact] 属性行=24）
        t3 = [f for f in report.findings
              if f.check == "T3" and f.path.endswith("AttrWindowTests.cs")]
        check(len(t3) == 1 and t3[0].line == 24,
              f"仅无标签对照类报 WARN（期望 1 条@24，实际 {[(f.line) for f in t3]}）")

        # ② 全仓 T3 计数：干净基线 4 方法 + 本样例 3 方法 = 7，关联 6
        by_id = {c["id"]: c for c in report.checks}
        check(by_id["T3"]["warn"] == 1 and "6/7" in by_id["T3"]["note"],
              f"T3 计数 6/7、1 WARN（实际 {by_id['T3']['note']}）")

        # ③ 直接断言类头窗谓词：隔属性行的长注释块标签命中；helper 形态
        #    外围类带标签、helper 类自身不带
        lines = T3_ATTR_WINDOW_TESTS_CS.splitlines()
        tagged = rs._tagged_class_lines(lines)
        check(tagged == {9, 33},
              f"带标签类声明行集合={{{9}, {33}}}（实际 {sorted(tagged)}）")
        check(not rs._class_header_has_tag(lines, 21),
              "无标签对照类头窗判 False（窗口放宽不放过真缺口）")

        check(report.exit_code == 0, "T3 WARN 不影响退出码（WARNING 级）")
    finally:
        shutil.rmtree(t, ignore_errors=True)


def main() -> int:
    try:
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, ValueError):
        pass
    print(f"selftest: {os.path.join('tools', 'ci', 'redline_scan.py')}")
    print(f"python: {sys.version.split()[0]}")
    test_clean_tree_passes()
    test_dirty_tree_caught()
    test_exemptions_work()
    test_cli_exit_codes_and_json()
    test_t8_predicate_table()
    test_t8_end_to_end()
    test_r7_numeric_anchors()
    test_t3_class_header_window()
    print("-" * 60)
    print(f"selftest summary: {PASS} passed, {FAIL} failed")
    return 1 if FAIL else 0


if __name__ == "__main__":
    sys.exit(main())
