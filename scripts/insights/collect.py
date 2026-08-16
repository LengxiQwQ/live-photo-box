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
    }


# ── C. 渲染 SVG 趋势图 ────────────────────────────────────────────

def render_chart(merged: dict, out_path: Path, theme: str) -> None:
    """自绘简约折线图：Views（实线）+ Clones（虚线），浅/深两版。"""
    if theme == "dark":
        c_views, c_clones, c_grid, c_text = "#4ea1ff", "#9aa4af", "#2d333b", "#9db1c4"
    else:
        c_views, c_clones, c_grid, c_text = "#0078d7", "#6e7681", "#eaeef2", "#57606a"

    days = sorted(set(merged["views"]) | set(merged["clones"]))
    if len(days) > DAYS_IN_WINDOW:
        days = days[-DAYS_IN_WINDOW:]
    views_pts = [merged["views"].get(d, {}).get("count", 0) for d in days]
    clones_pts = [merged["clones"].get(d, {}).get("count", 0) for d in days]

    W, H = 780, 220
    pad_l, pad_r, pad_t, pad_b = 52, 12, 28, 30
    plot_w, plot_h = W - pad_l - pad_r, H - pad_t - pad_b
    n = len(days)

    if n == 0:
        svg = (
            f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" '
            f'viewBox="0 0 {W} {H}" font-family="system-ui, -apple-system, Segoe UI, sans-serif">'
            f'<text x="{W/2}" y="{H/2}" text-anchor="middle" font-size="13" fill="{c_text}">'
            "No data yet</text></svg>"
        )
        out_path.write_text(svg, encoding="utf-8")
        return

    max_val = max(max(views_pts), max(clones_pts), 1)
    y_max = max(max_val * 1.15, 4.0)

    def y(v: float) -> float:
        return pad_t + plot_h * (1 - v / y_max)

    def x(i: int) -> float:
        return pad_l + plot_w * i / max(1, n - 1)

    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{W}" height="{H}" viewBox="0 0 {W} {H}" '
             f'font-family="system-ui, -apple-system, Segoe UI, sans-serif">']

    # y 网格线 + 刻度
    for g in range(5):
        v = y_max * g / 4
        yy = y(v)
        parts.append(f'<line x1="{pad_l:.0f}" y1="{yy:.1f}" x2="{W - pad_r:.0f}" y2="{yy:.1f}" '
                     f'stroke="{c_grid}" stroke-width="1"/>')
        parts.append(f'<text x="{pad_l - 8:.0f}" y="{yy + 4:.1f}" text-anchor="end" font-size="11" '
                     f'fill="{c_text}">{fmt_num(round(v))}</text>')

    # x 轴日期标签（首 / 中 / 尾）
    for i in sorted(set([0, n // 2, n - 1])):
        parts.append(f'<text x="{x(i):.1f}" y="{H - 10:.0f}" text-anchor="middle" font-size="11" '
                     f'fill="{c_text}">{days[i][5:]}</text>')

    # 折线
    def polyline(points, stroke, sw, dash):
        pts = " ".join(f"{x(i):.1f},{y(p):.1f}" for i, p in enumerate(points))
        return (f'<polyline points="{pts}" fill="none" stroke="{stroke}" stroke-width="{sw}" '
                f'stroke-linejoin="round" stroke-linecap="round"'
                + (f' stroke-dasharray="{dash}"' if dash else "") + "/>")

    if n > 1:
        parts.append(polyline(views_pts, c_views, 2.5, None))
        parts.append(polyline(clones_pts, c_clones, 2, "6 4"))

    # 末点圆点 + 末值标注（Views 上方、Clones 下方，避免重叠）
    last_v, last_c = views_pts[-1], clones_pts[-1]
    lx = x(n - 1)
    if last_v > 0:
        parts.append(f'<circle cx="{lx:.1f}" cy="{y(last_v):.1f}" r="3.5" fill="{c_views}"/>')
        parts.append(f'<text x="{lx - 6:.1f}" y="{y(last_v) - 8:.1f}" text-anchor="end" font-size="11" '
                     f'font-weight="600" fill="{c_views}">{fmt_num(last_v)}</text>')
    if last_c > 0:
        parts.append(f'<circle cx="{lx:.1f}" cy="{y(last_c):.1f}" r="3" fill="{c_clones}"/>')
        parts.append(f'<text x="{lx + 6:.1f}" y="{y(last_c) + 14:.1f}" font-size="11" '
                     f'font-weight="600" fill="{c_clones}">{fmt_num(last_c)}</text>')

    # 图例（左上）
    parts.append(f'<line x1="{pad_l:.0f}" y1="14" x2="{pad_l + 16:.0f}" y2="14" stroke="{c_views}" stroke-width="2.5"/>')
    parts.append(f'<text x="{pad_l + 20:.0f}" y="18" font-size="11" fill="{c_text}">Views</text>')
    parts.append(f'<line x1="{pad_l + 62:.0f}" y1="14" x2="{pad_l + 78:.0f}" y2="14" stroke="{c_clones}" stroke-width="2" stroke-dasharray="6 4"/>')
    parts.append(f'<text x="{pad_l + 82:.0f}" y="18" font-size="11" fill="{c_text}">Clones</text>')

    parts.append("</svg>")
    out_path.write_text("".join(parts), encoding="utf-8")
    log(f"rendered -> {out_path.name}")


# ── D. 更新 README 区块 ───────────────────────────────────────────

def render_section(metrics: dict, refs: list, paths: list, updated_at: str, lang: str,
                   repo: str = "") -> str:
    """生成双语 README 数据区块（替换 INSIGHTS 占位符之间的内容）。"""
    def top5(items, key):
        names = []
        for it in items[:10]:
            name = (it.get(key) or "").lstrip("/")
            if key == "path" and repo:
                # GitHub paths 端点返回 /{owner}/{repo}/... 前缀，剥掉并去冗余段
                low = name.lower()
                if low.startswith(repo.lower()):
                    name = name[len(repo):]
                name = re.sub(r"^/?(blob|tree)/[^/]+/", "", name)
                name = name.strip("/")
                name = "Home" if name == "" else name
            if name and name not in names:
                names.append(name)
            if len(names) >= 5:
                break
        return " · ".join(names) if names else "—"

    if lang == "zh":
        title_alt = "仓库浏览量 & 克隆量趋势"
        rows = [
            ("浏览 · 最近 14 天", fmt_num(metrics["views_14"]), fmt_num(metrics["views_14_uniq"])),
            ("克隆 · 最近 14 天", fmt_num(metrics["clones_14"]), fmt_num(metrics["clones_14_uniq"])),
            ("浏览 · 累计", fmt_num(metrics["views_all"]), "—"),
            ("克隆 · 累计", fmt_num(metrics["clones_all"]), "—"),
        ]
        table = "\n".join(f"| {a} | {b} | {c} |" for a, b, c in rows)
        refs_line = "**热门来源：** " + top5(refs, "referrer")
        paths_line = "**热门内容：** " + top5(paths, "path")
        updated = f"*最后更新：{updated_at}*"
    else:
        title_alt = "Repository views & clones over time"
        rows = [
            ("Views · last 14 days", fmt_num(metrics["views_14"]), fmt_num(metrics["views_14_uniq"])),
            ("Clones · last 14 days", fmt_num(metrics["clones_14"]), fmt_num(metrics["clones_14_uniq"])),
            ("Views · all-time", fmt_num(metrics["views_all"]), "—"),
            ("Clones · all-time", fmt_num(metrics["clones_all"]), "—"),
        ]
        table = "\n".join(f"| {a} | {b} | {c} |" for a, b, c in rows)
        refs_line = "**Top referrers:** " + top5(refs, "referrer")
        paths_line = "**Top content:** " + top5(paths, "path")
        updated = f"*Last updated: {updated_at}*"

    header = "| Metric | Count | Uniques |" if lang == "en" else "| 指标 | 次数 | 独立数 |"
    divider = "|---|---|---|"

    return (
        "<!-- INSIGHTS:START -->\n"
        '<p align="center"><picture>\n'
        '  <source media="(prefers-color-scheme: dark)" srcset="insights/chart-dark.svg">\n'
        f'  <img src="insights/chart.svg" alt="{title_alt}" width="780">\n'
        "</picture></p>\n\n"
        f"{header}\n{divider}\n{table}\n\n"
        f"{refs_line}  \n{paths_line}  \n{updated}\n"
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

    render_chart(merged, root / "insights" / "chart.svg", "light")
    render_chart(merged, root / "insights" / "chart-dark.svg", "dark")

    metrics = compute_metrics(merged, data)
    refs = ((data.get("referrers") or []) if isinstance(data.get("referrers"), list) else [])
    paths = ((data.get("paths") or []) if isinstance(data.get("paths"), list) else [])
    updated_at = payload["fetched_at"].replace("T", " ").replace("Z", " UTC")

    update_readme(root, render_section(metrics, refs, paths, updated_at, "en", repo), "README.md")
    update_readme(root, render_section(metrics, refs, paths, updated_at, "zh", repo), "README.zh-CN.md")

    log("done")


if __name__ == "__main__":
    sys.exit(main())
