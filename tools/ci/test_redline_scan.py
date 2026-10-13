#!/usr/bin/env python3
"""
test_redline_scan.py — redline_scan.py 自测（任务 T-027；零三方依赖，
`python3 test_redline_scan.py` 直跑）。

用例（正反样例 + 豁免 + CLI 端到端）：
  1. clean_tree_passes        正样例：干净仓库 7 项检查全 PASS、退出码 0
  2. dirty_tree_caught        反样例：故意违规的临时仓库逐条被抓到
                              （检查 ID/文件/行号一一对应，无漏报无多报）
  3. exemptions_work          ci-allow 行级豁免 + allowlist 路径/节级豁免生效
  4. cli_exit_codes_and_json  命令行入口退出码与 JSON 报告结构

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
        capture_output=True, text=True, timeout=60)
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
        check(len(report.checks) == 7, f"运行全部 7 项检查（实际 {len(report.checks)}）")
        check(all(c["status"] == "PASS" for c in report.checks),
              "干净样例 7 项全部 PASS：" +
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
            capture_output=True, text=True, timeout=120)
        check(r.returncode == 0, f"干净仓库 CLI 退出码 0（实际 {r.returncode}）")
        with open(json_path, "r", encoding="utf-8") as fh:
            data = json.load(fh)
        check(data["exit_code"] == 0 and data["counts"]["fail_findings"] == 0,
              "JSON 报告结构与 exit_code 字段正确")
        check(len(data["checks"]) == 7 and all(
            c["status"] == "PASS" for c in data["checks"]),
            "JSON 检查表 7 项全 PASS")

        build_dirty_root(t)
        baseline2 = init_repo(t)
        r2 = subprocess.run(
            [sys.executable, scanner, "--root", t, "--baseline", "0" * 40],
            capture_output=True, text=True, timeout=120)
        check(r2.returncode == 1, f"违规仓库 CLI 退出码 1（实际 {r2.returncode}）")
        check("R13" in r2.stdout and "R9" in r2.stdout,
              "人读报告包含 R9/R13 检查行")
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
    print("-" * 60)
    print(f"selftest summary: {PASS} passed, {FAIL} failed")
    return 1 if FAIL else 0


if __name__ == "__main__":
    sys.exit(main())
