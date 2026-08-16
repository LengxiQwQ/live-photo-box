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
DAYS_IN_WINDOW = 45  # 趋势图显示最近 45 天（不足则全部历史）


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


# ── C. 渲染 SVG 信息图卡片 ────────────────────────────────────────

def render_card(merged: dict, metrics: dict, refs: list, updated_at: str,
                out_path: Path, theme: str) -> None:
    """渲染 metrics 风格信息图卡片：渐变背景 + 圆角 + 大数字 + 迷你趋势 + 来源排行。"""
    if theme == "dark":
        bg1, bg2 = "#0d1117", "#1a2233"
        border, text, sub = "#30363d", "#e6edf3", "#8b949e"
        accent, accent2 = "#4ea1ff", "#7ee787"
        chip_bg = "rgba(255,255,255,0.06)"
    else:
        bg1, bg2 = "#ffffff", "#f6f8fa"
        border, text, sub = "#d0d7de", "#24292f", "#57606a"
        accent, accent2 = "#0969da", "#1a7f37"
        chip_bg = "rgba(9,105,218,0.05)"

    W, H, pad = 780, 270, 22
    p = [
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" '
        f'viewBox="0 0 {W} {H}" font-family="system-ui, -apple-system, Segoe UI, sans-serif">',
        "<defs>"
        '<linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">'
        f'<stop offset="0" stop-color="{bg1}"/><stop offset="1" stop-color="{bg2}"/></linearGradient>'
        '<linearGradient id="sparkfill" x1="0" y1="0" x2="0" y2="1">'
        f'<stop offset="0" stop-color="{accent}" stop-opacity="0.4"/>'
        f'<stop offset="1" stop-color="{accent}" stop-opacity="0"/></linearGradient>'
        "</defs>",
        f'<rect x="0" y="0" width="{W}" height="{H}" rx="14" fill="url(#bg)" stroke="{border}" stroke-width="1"/>',
    ]

    # 标题 + 数据范围
    first = metrics.get("first_date") or "—"
    p.append(f'<circle cx="{pad + 8}" cy="30" r="4" fill="{accent}"/>')
    p.append(f'<text x="{pad + 20}" y="35" font-size="15" font-weight="700" fill="{text}">Repository Traffic</text>')
    p.append(f'<text x="{W - pad}" y="35" text-anchor="end" font-size="11" fill="{sub}">'
             f'{first} → {updated_at[:16]} UTC</text>')

    # 2x2 数字块
    blocks = [
        ("Views",   metrics["views_all"],      "all-time"),
        ("Uniques", metrics["views_14_uniq"],  "14-day"),
        ("Clones",  metrics["clones_all"],     "all-time"),
        ("Cloners", metrics["clones_14_uniq"], "14-day"),
    ]
    bw = (W - pad * 2 - 24) / 2
    bh = 62
    for i, (label, value, sublabel) in enumerate(blocks):
        r, c = divmod(i, 2)
        bx = pad + c * (bw + 24)
        by = 52 + r * (bh + 12)
        p.append(f'<rect x="{bx:.1f}" y="{by}" width="{bw:.1f}" height="{bh}" rx="10" '
                 f'fill="{chip_bg}" stroke="{border}" stroke-width="1"/>')
        p.append(f'<text x="{bx + bw - 14:.1f}" y="{by + 22}" text-anchor="end" font-size="10" fill="{sub}">{sublabel}</text>')
        p.append(f'<text x="{bx + 16:.1f}" y="{by + 26}" font-size="12" fill="{sub}">{label}</text>')
        p.append(f'<text x="{bx + 16:.1f}" y="{by + 53}" font-size="27" font-weight="800" fill="{text}">{fmt_num(value)}</text>')

    # 迷你趋势（Views sparkline）
    days = sorted(merged["views"])
    series = [merged["views"][d]["count"] for d in days][-DAYS_IN_WINDOW:]
    n = len(series)
    sy = 200
    p.append(f'<text x="{pad}" y="{sy - 8}" font-size="11" fill="{sub}">Views trend · last {n} days</text>')
    if n >= 2:
        sw = W - pad * 2
        sh = 26
        maxv = max(series) or 1
        pts = " ".join(f"{pad + sw * i / (n - 1):.1f},{sy + sh * (1 - v / maxv):.1f}"
                       for i, v in enumerate(series))
        p.append(f'<polygon points="{pad},{sy + sh} {pts} {pad + sw},{sy + sh}" fill="url(#sparkfill)"/>')
        p.append(f'<polyline points="{pts}" fill="none" stroke="{accent}" stroke-width="2" '
                 f'stroke-linejoin="round" stroke-linecap="round"/>')
        lx, ly = pad + sw, sy + sh * (1 - series[-1] / maxv)
        p.append(f'<circle cx="{lx:.1f}" cy="{ly:.1f}" r="3" fill="{accent}"/>')
        p.append(f'<text x="{lx - 6:.1f}" y="{ly - 7:.1f}" text-anchor="end" font-size="11" '
                 f'font-weight="600" fill="{accent}">{fmt_num(series[-1])}</text>')

    # Top referrers
    top = " · ".join(x.get("referrer", "") for x in refs[:5] if x.get("referrer"))
    if top:
        p.append(f'<text x="{pad}" y="258" font-size="11" fill="{sub}">Top referrers: '
                 f'<tspan fill="{text}">{top}</tspan></text>')

    p.append("</svg>")
    out_path.write_text("".join(p), encoding="utf-8")
    log(f"rendered -> {out_path.name}")


# ── D. 更新 README 区块 ───────────────────────────────────────────

def render_section(metrics: dict, updated_at: str, lang: str) -> str:
    """生成双语 README 区块（图片卡片 + 数据范围 + 口径脚注）。"""
    if lang == "zh":
        title_alt = "仓库流量统计"
        updated = (f"*数据开始：{metrics.get('first_date') or '—'} · 最后更新：{updated_at}*  \n"
                   "*独立数 = 近 14 天窗口内的独立访客/克隆者；跨天独立访客不可累加。*")
    else:
        title_alt = "Repository traffic"
        updated = (f"*Data since {metrics.get('first_date') or '—'} · Last updated: {updated_at}*  \n"
                   "*Uniques = distinct visitors/cloners in the last 14-day window; "
                   "cross-day uniques can't be summed.*")

    return (
        "<!-- INSIGHTS:START -->\n"
        '<p align="center"><picture>\n'
        '  <source media="(prefers-color-scheme: dark)" srcset="insights/chart-dark.svg">\n'
        f'  <img src="insights/chart.svg" alt="{title_alt}" width="780">\n'
        "</picture></p>\n\n"
        f"{updated}\n"
        "<!-- INSIGHTS:END -->"
    )


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
    updated_at = payload["fetched_at"].replace("T", " ").replace("Z", " UTC")

    render_card(merged, metrics, refs, updated_at, root / "insights" / "chart.svg", "light")
    render_card(merged, metrics, refs, updated_at, root / "insights" / "chart-dark.svg", "dark")

    update_readme(root, render_section(metrics, updated_at, "en"), "README.md")
    update_readme(root, render_section(metrics, updated_at, "zh"), "README.zh-CN.md")

    log("done")


if __name__ == "__main__":
    sys.exit(main())
