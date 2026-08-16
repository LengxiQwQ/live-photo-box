#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""GitHub 仓库洞察采集脚本（Repository Insights Collector）

每天抓取 GitHub Traffic API 数据并永久存档，防止 14 天窗口过期丢失：

  1. 抓取 5 个 REST 端点（views / clones / referrers / paths / repo 元数据）
  2. 原始完整响应 → insights/raw/<UTC时间戳>.json   （永久归档，零丢弃）
  3. 整理存档     → insights/traffic.json
       views/clones:          按日期 upsert（14 天内刷新，窗口外保留）
       referrers/paths/repo:  快照追加（带 fetched_at，历史全保留）
  4. 渲染趋势图   → insights/chart.svg + chart-dark.svg（浅/深色自适应）
  5. 更新 README  → README.md / README.zh-CN.md 的 <!-- INSIGHTS:START/END --> 区块

环境变量：
  TRAFFIC_TOKEN     必需：带 public_repo/repo scope 的 PAT（GITHUB_TOKEN 无法读 traffic API）
  GITHUB_REPOSITORY 可选：owner/repo；不设时从 git remote 推导
命令行参数：
  --root <dir>  输出根目录（默认：脚本所在仓库根；本地验证时指向 _ai-tmp 副本）
"""

from __future__ import annotations

import argparse
import datetime as dt
import json
import os
import re
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

API_BASE = "https://api.github.com"


# ── 基础工具 ───────────────────────────────────────────────────────

def log(msg: str) -> None:
    print(f"[insights] {msg}", flush=True)


def resolve_repo() -> str:
    """解析 owner/repo，优先 GITHUB_REPOSITORY，否则从 git remote 推导。"""
    repo = os.environ.get("GITHUB_REPOSITORY")
    if repo:
        return repo.strip().strip("/")
    try:
        remote = subprocess.check_output(
            ["git", "remote", "get-url", "origin"], text=True, stderr=subprocess.DEVNULL
        ).strip()
    except Exception:
        raise SystemExit(
            "Cannot resolve repo: set GITHUB_REPOSITORY or run inside a git repository"
        )
    m = re.search(r"(?:https?://|git@)[^/:]+[:/]([^/]+/[^/]+?)(?:\.git)?$", remote)
    if not m:
        raise SystemExit(f"Cannot parse git remote: {remote}")
    return m.group(1)


def api_get(token: str, path: str, retries: int = 3):
    """GET 一个 REST 端点，指数退避重试；403/404/网络错误可恢复则继续。"""
    headers = {
        "Authorization": f"Bearer {token}",
        "Accept": "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "live-photo-box-insights",
    }
    url = API_BASE + path
    for attempt in range(1, retries + 1):
        req = urllib.request.Request(url, headers=headers)
        try:
            with urllib.request.urlopen(req, timeout=30) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            if e.code == 403 and e.headers.get("X-RateLimit-Remaining") == "0":
                reset = int(e.headers.get("X-RateLimit-Reset", "0"))
                wait = min(max(1, reset - int(time.time())), 60)
                log(f"rate limited, waiting {wait}s...")
                time.sleep(wait)
                continue
            if e.code == 404:
                log(f"  {path} -> 404 (endpoint not available)")
                return None
            if attempt < retries:
                log(f"  {path} HTTP {e.code}, retry {attempt}/{retries}")
                time.sleep(2 ** attempt)
                continue
            raise
        except (urllib.error.URLError, TimeoutError) as e:
            if attempt < retries:
                log(f"  {path} network error, retry {attempt}/{retries}")
                time.sleep(2 ** attempt)
                continue
            raise
    return None


def fmt_num(v) -> str:
    return f"{int(v):,}"


# ── A. 抓取 ────────────────────────────────────────────────────────

def collect(token: str, repo: str) -> dict:
    endpoints = {
        "views":     f"/repos/{repo}/traffic/views",
        "clones":    f"/repos/{repo}/traffic/clones",
        "referrers": f"/repos/{repo}/traffic/popular/referrers",
        "paths":     f"/repos/{repo}/traffic/popular/paths",
        "repo_meta": f"/repos/{repo}",
    }
    results = {}
    for key, path in endpoints.items():
        log(f"fetching {path}")
        results[key] = api_get(token, path)
    return results


# ── B. 全量归档 + 整理合并 ────────────────────────────────────────

def archive_raw(root: Path, payload: dict) -> dict:
    """原始完整响应永久归档到 insights/raw/，永不覆盖。"""
    ts_file = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    raw_dir = root / "insights" / "raw"
    raw_dir.mkdir(parents=True, exist_ok=True)
    out = raw_dir / f"{ts_file}.json"
    if out.exists():  # 同一分钟重复跑，避免覆盖
        out = raw_dir / f"{ts_file}-{int(time.time() * 1000) % 1000}.json"
    out.write_text(json.dumps(payload, indent=2, ensure_ascii=False), encoding="utf-8")
    log(f"archived raw -> insights/raw/{out.name}")
    return payload


def merge(payload: dict, traffic_path: Path) -> dict:
    """把本次抓取合并进存档：views/clones 按日 upsert，其余快照追加。"""
    data = payload.get("data", {})
    fetched_at = payload.get("fetched_at", "")
    existing: dict = {}
    if traffic_path.exists():
        try:
            existing = json.loads(traffic_path.read_text(encoding="utf-8"))
        except Exception as e:
            log(f"WARNING: corrupted {traffic_path.name}, starting fresh: {e}")

    views: dict = existing.get("views", {})
    clones: dict = existing.get("clones", {})

    v = data.get("views") or {}
    for day in v.get("views", []):
        date = (day.get("timestamp") or "")[:10]
        if date:
            views[date] = {"count": day.get("count", 0), "uniques": day.get("uniques", 0)}
    c = data.get("clones") or {}
    for day in c.get("clones", []):
        date = (day.get("timestamp") or "")[:10]
        if date:
            clones[date] = {"count": day.get("count", 0), "uniques": day.get("uniques", 0)}

    result = {
        "views": dict(sorted(views.items())),
        "clones": dict(sorted(clones.items())),
        "referrer_snapshots": list(existing.get("referrer_snapshots", [])),
        "path_snapshots": list(existing.get("path_snapshots", [])),
        "repo_meta_snapshots": list(existing.get("repo_meta_snapshots", [])),
        "updated_at": fetched_at,
    }

    if data.get("referrers") is not None:
        result["referrer_snapshots"].append(
            {"fetched_at": fetched_at, "referrers": data["referrers"]}
        )
    if data.get("paths") is not None:
        result["path_snapshots"].append(
            {"fetched_at": fetched_at, "paths": data["paths"]}
        )
    if data.get("repo_meta") is not None:
        m = data["repo_meta"]
        result["repo_meta_snapshots"].append(
            {
                "fetched_at": fetched_at,
                "stars": m.get("stargazers_count"),
                "forks": m.get("forks_count"),
                "watchers": m.get("subscribers_count"),
                "open_issues": m.get("open_issues_count"),
                "network": m.get("network_count"),
                "pushed_at": m.get("pushed_at"),
            }
        )
    return result


def compute_metrics(merged: dict, data: dict) -> dict:
    """从最新抓取算 14 天聚合，从存档算累计。"""
    views_latest = data.get("views") or {}
    clones_latest = data.get("clones") or {}
    return {
        "views_14": views_latest.get("count", 0),
        "views_14_uniq": views_latest.get("uniques", 0),
        "clones_14": clones_latest.get("count", 0),
        "clones_14_uniq": clones_latest.get("uniques", 0),
        "views_all": sum(d.get("count", 0) for d in merged["views"].values()),
        "clones_all": sum(d.get("count", 0) for d in merged["clones"].values()),
        "first_date": min(merged["views"]) if merged["views"] else "",
    }


# ── D. 更新 README 区块 ───────────────────────────────────────────

def render_section(metrics: dict, refs: list, updated_at: str, lang: str) -> str:
    """生成双语 README 极简数据区块：纯 Markdown 文本，无图标/表格/图片。"""
    first = metrics.get("first_date") or "—"
    top = " · ".join(x.get("referrer", "") for x in refs[:6] if x.get("referrer")) or "—"

    if lang == "zh":
        lines = [
            "**📊 仓库流量**",
            "",
            f"访问次数：**{fmt_num(metrics['views_all'])}** ｜ 不重复访客：**{fmt_num(metrics['views_14_uniq'])}**（近 14 天） ｜ 仓库克隆：**{fmt_num(metrics['clones_all'])}** ｜ 不重复克隆：**{fmt_num(metrics['clones_14_uniq'])}**（近 14 天）",
            "",
            f"**热门来源：** {top}",
            "",
            f"> 数据开始：{first} · 最后更新：{updated_at}",
        ]
    else:
        lines = [
            "**📊 Repository Traffic**",
            "",
            f"Views: **{fmt_num(metrics['views_all'])}** ｜ Uniques: **{fmt_num(metrics['views_14_uniq'])}** (14-day) ｜ Clones: **{fmt_num(metrics['clones_all'])}** ｜ Cloners: **{fmt_num(metrics['clones_14_uniq'])}** (14-day)",
            "",
            f"**Top referrers:** {top}",
            "",
            f"> Data since {first} · Last updated: {updated_at}",
        ]

    return "<!-- INSIGHTS:START -->\n" + "\n".join(lines) + "\n<!-- INSIGHTS:END -->"


def update_readme(root: Path, section: str, filename: str) -> None:
    path = root / filename
    if not path.exists():
        log(f"SKIP {filename}: file not found")
        return
    text = path.read_text(encoding="utf-8")
    pattern = re.compile(r"<!-- INSIGHTS:START -->.*?<!-- INSIGHTS:END -->", re.DOTALL)
    if not pattern.search(text):
        log(f"SKIP {filename}: no INSIGHTS placeholder block")
        return
    new_text = pattern.sub(section, text, count=1)
    if new_text != text:
        path.write_text(new_text, encoding="utf-8")
        log(f"updated {filename}")
    else:
        log(f"{filename}: unchanged")


# ── 入口 ──────────────────────────────────────────────────────────

def main() -> None:
    parser = argparse.ArgumentParser(description="GitHub repository insights collector")
    parser.add_argument("--root", default=None, help="output root (default: repo root)")
    args = parser.parse_args()

    root = Path(args.root).resolve() if args.root else Path(__file__).resolve().parent.parent.parent
    token = os.environ.get("TRAFFIC_TOKEN")
    if not token:
        raise SystemExit("TRAFFIC_TOKEN env not set (GITHUB_TOKEN cannot read the traffic API)")

    repo = resolve_repo()
    log(f"repo={repo}  root={root}")

    data = collect(token, repo)
    if data.get("views") is None and data.get("clones") is None:
        log("WARNING: no traffic data fetched at all")

    payload = {"fetched_at": dt.datetime.now(dt.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ"),
               "data": data}
    archive_raw(root, payload)

    merged = merge(payload, root / "insights" / "traffic.json")
    traffic_path = root / "insights" / "traffic.json"
    traffic_path.parent.mkdir(parents=True, exist_ok=True)
    traffic_path.write_text(json.dumps(merged, indent=2, ensure_ascii=False), encoding="utf-8")
    log(f"wrote {traffic_path.name} (views={len(merged['views'])} days)")

    metrics = compute_metrics(merged, data)
    refs = ((data.get("referrers") or []) if isinstance(data.get("referrers"), list) else [])
    fetched = dt.datetime.fromisoformat(payload["fetched_at"].replace("Z", "+00:00"))
    cn = fetched.astimezone(dt.timezone(dt.timedelta(hours=8)))
    updated_at = cn.strftime("%Y-%m-%d") + " (UTC+8)"

    update_readme(root, render_section(metrics, refs, updated_at, "en"), "README.md")
    update_readme(root, render_section(metrics, refs, updated_at, "zh"), "README.zh-CN.md")

    log("done")


if __name__ == "__main__":
    sys.exit(main())
