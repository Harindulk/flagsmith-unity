"""Builds the website into _site/: the landing page from site/, and one docs page per section of the package guide.

    python3 -m pip install markdown==3.7
    python3 site/build.py            # then serve _site/, e.g. python3 -m http.server -d _site

The guide (com.flagsmith.unity/Documentation~/index.md) stays the single source, so the docs ship inside the package too.
"""
import html
import re
import shutil
from pathlib import Path

import markdown

ROOT = Path(__file__).resolve().parent.parent
SITE, OUT = ROOT / "site", ROOT / "_site"
GUIDE = ROOT / "com.flagsmith.unity" / "Documentation~" / "index.md"


def slug(text):
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")


def split_guide(text):
    """Sections at '## ' headings (outside code fences). Text before the first one is dropped (title and contents list)."""
    pages, current, fenced = [], None, False
    for line in text.splitlines():
        if line.startswith("```"):
            fenced = not fenced
        if not fenced and line.startswith("## "):
            current = {"title": line[3:].strip(), "lines": []}
            pages.append(current)
        elif current is not None:
            current["lines"].append(line)
    for i, p in enumerate(pages):
        p["file"] = "index.html" if i == 0 else slug(p["title"]) + ".html"
        p["md"] = "\n".join(p["lines"]).strip() + "\n"
    return pages


def page_html(p, pages, body):
    nav = "\n".join(
        f'<a href="{q["file"]}"{" aria-current=page" if q is p else ""}>{html.escape(q["title"])}</a>' for q in pages)
    i = pages.index(p)
    prev = f'<a href="{pages[i-1]["file"]}"><small>Previous</small>{html.escape(pages[i-1]["title"])}</a>' if i else "<span></span>"
    nxt = (f'<a class="next" href="{pages[i+1]["file"]}"><small>Next</small>{html.escape(pages[i+1]["title"])}</a>'
           if i + 1 < len(pages) else "<span></span>")
    title = "Flagsmith for Unity docs" if i == 0 else f'{p["title"]} · Flagsmith for Unity'
    return f"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{html.escape(title)}</title>
<meta name="description" content="Flagsmith for Unity documentation: {html.escape(p["title"])}.">
<link rel="icon" href="data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 32 32'%3E%3Crect width='32' height='32' rx='6' fill='%23141721'/%3E%3Crect x='9' y='9' width='14' height='14' rx='2' fill='%23ffb020' transform='rotate(12 16 16)'/%3E%3C/svg%3E">
<link rel="preconnect" href="https://fonts.googleapis.com">
<link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
<link href="https://fonts.googleapis.com/css2?family=Albert+Sans:wght@400;500;600&display=swap" rel="stylesheet">
<link rel="stylesheet" href="../assets/site.css">
</head>
<body>
<header class="bar">
  <a class="mark" href="../" aria-label="Flagsmith for Unity, home">
    <svg viewBox="0 0 32 32" aria-hidden="true"><rect width="32" height="32" rx="6" fill="#141721"/><rect x="9" y="9" width="14" height="14" rx="2" fill="#ffb020" transform="rotate(12 16 16)"/></svg>
    Flagsmith for Unity
  </a>
  <nav aria-label="Main">
    <button class="btn ink menu-btn" type="button" aria-expanded="false" aria-controls="docs-nav">Contents</button>
    <a href="./" aria-current="page">Docs</a>
    <a class="hide-s" href="../#play">Playground</a>
    <a href="https://github.com/Harindulk/flagsmith-unity">GitHub</a>
  </nav>
</header>
<div class="docs">
  <nav class="docs-nav" id="docs-nav" aria-label="Documentation">
    <b>Guide</b>
    {nav}
  </nav>
  <main class="doc">
    <h1>{html.escape("Flagsmith for Unity" if i == 0 else p["title"])}</h1>
{body}
    <nav class="pager" aria-label="Pages">{prev}{nxt}</nav>
  </main>
</div>
<footer class="foot">
  <div class="stmt"><p>A community package for Flagsmith, MIT licensed. Not an official Flagsmith SDK.</p><small>Docs built from the package's own guide.</small></div>
  <div class="nav"><b>Docs</b><ul><li><a href="./">Guide</a></li><li><a href="api-reference.html">API reference</a></li><li><a href="troubleshooting.html">Troubleshooting</a></li></ul></div>
  <div class="made"><b>Version</b><p>0.1.0<br><small>Unity 6000.0+</small></p></div>
  <div class="repo"><b>Source and issues</b><a href="https://github.com/Harindulk/flagsmith-unity">github.com/Harindulk/flagsmith-unity</a></div>
</footer>
<script src="../assets/site.js"></script>
</body>
</html>
"""


def main():
    if OUT.exists():
        shutil.rmtree(OUT)
    shutil.copytree(SITE, OUT, ignore=shutil.ignore_patterns("build.py", "__pycache__"))
    shutil.copytree(ROOT / "docs" / "media", OUT / "media", dirs_exist_ok=True)

    pages = split_guide(GUIDE.read_text(encoding="utf-8"))
    # Which page each heading lives on, so '#anchor' links work across pages.
    where = {}
    for p in pages:
        where[slug(p["title"])] = p["file"]
        for h in re.findall(r"^###+ (.+)$", p["md"], re.M):
            where.setdefault(slug(h), p["file"])

    (OUT / "docs").mkdir()
    for p in pages:
        md = markdown.Markdown(extensions=["fenced_code", "tables", "toc"],
                               extension_configs={"toc": {"slugify": lambda t, _sep: slug(t)}})
        body = md.convert(p["md"])
        body = body.replace('src="../../docs/media/', 'src="../media/')
        body = body.replace('href="../../README.md"', 'href="../"')

        def anchor(m):
            target = m.group(1)
            page = where.get(target)
            if page is None or page == p["file"]:
                return m.group(0)
            return f'href="{page}' + ("" if target == slug(next(q["title"] for q in pages if q["file"] == page)) else f"#{target}") + '"'
        body = re.sub(r'href="#([^"]+)"', anchor, body)
        body = body.replace("<img ", '<img loading="lazy" ')
        (OUT / "docs" / p["file"]).write_text(page_html(p, pages, body), encoding="utf-8")
    write_llms(pages)
    print(f"built {len(pages)} docs pages into {OUT.relative_to(ROOT)}/")


def write_llms(pages):
    """llms.txt (an index for agents) and llms-full.txt (the whole guide plus the Agent Skill in one file)."""
    site = "https://harindulk.github.io/flagsmith-unity/"
    lines = ["# Flagsmith for Unity", "",
             "> Open-source Unity package for Flagsmith: feature flags, remote config, live tuning and A/B tests "
             "(Flagsmith Experimentation) for Unity 6+ games, WebGL included. MIT licensed, community-made.", "",
             "Install from git URL in Unity Package Manager: "
             "https://github.com/Harindulk/flagsmith-unity.git?path=/com.flagsmith.unity", "",
             "## Docs", ""]
    lines += [f"- [{p['title']}]({site}docs/{p['file']})" for p in pages]
    lines += ["", "## For coding agents", "",
              "- [Agent Skill: set up Flagsmith or run an A/B test in a Unity project]"
              "(https://github.com/Harindulk/flagsmith-unity/tree/main/skills/flagsmith-unity)",
              f"- [Everything in one file]({site}llms-full.txt)", ""]
    (OUT / "llms.txt").write_text("\n".join(lines), encoding="utf-8")
    skill = ROOT / "skills" / "flagsmith-unity"
    parts = [GUIDE.read_text(encoding="utf-8")]
    for f in ["SKILL.md", "references/setup.md", "references/experiment.md"]:
        parts.append(f"\n\n---\n\n<!-- skills/flagsmith-unity/{f} -->\n\n" + (skill / f).read_text(encoding="utf-8"))
    (OUT / "llms-full.txt").write_text("".join(parts), encoding="utf-8")


if __name__ == "__main__":
    main()
