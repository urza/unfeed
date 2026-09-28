#!/usr/bin/env python3
"""Rebuild the chapter content and TOC, preserving spec.html's UI shell.

Run from docs with: python3 tools/build_spec_html.py
Install the documentation-only dependency with tools/requirements.txt first.
"""

from html import escape
from pathlib import Path
import re

try:
    import markdown
except ImportError as error:
    raise SystemExit(
        "Install the documentation dependency: "
        "python3 -m pip install -r tools/requirements.txt"
    ) from error


ROOT = Path(__file__).resolve().parent.parent


def slug(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")


def render(text: str) -> str:
    return markdown.markdown(text.strip(), extensions=["tables", "fenced_code", "sane_lists"])


def chapter_parts(source: str):
    """Split level-two headings outside code fences; keep deeper headings intact."""
    lines = source.splitlines()
    if not lines or not lines[0].startswith("# "):
        raise ValueError("A chapter must start with its level-one title")
    title = lines[0][2:].strip()
    intro, sections, current = [], [], []
    heading = None
    fence = None
    for line in lines[1:]:
        marker = re.match(r"^\s{0,3}(`{3,}|~{3,})", line)
        if marker:
            token = marker.group(1)
            if fence is None:
                fence = token
            elif token[0] == fence[0] and len(token) >= len(fence):
                fence = None
        if fence is None and line.startswith("## "):
            if heading is None:
                intro = current
            else:
                sections.append((heading, "\n".join(current)))
            heading, current = line[3:].strip(), []
        else:
            current.append(line)
    if heading is None:
        intro = current
    else:
        sections.append((heading, "\n".join(current)))
    return title, "\n".join(intro), sections


def build() -> None:
    output = ROOT / "spec.html"
    shell = output.read_text(encoding="utf-8")
    chapters, toc, ids = [], [], set()
    sources = sorted(ROOT.glob("[0-9][0-9]-*.md"))
    if len(sources) != 17:
        raise ValueError(f"Expected 17 chapters, found {len(sources)}")
    for path in sources:
        title, intro, sections = chapter_parts(path.read_text(encoding="utf-8"))
        chapter_id = path.stem
        ids.add(chapter_id)
        part = [
            f'<details class="chapter" id="{chapter_id}" open>'
            f'<summary>{escape(title)}</summary><div class="body">'
            f'<div class="intro">{render(intro)}</div>'
        ]
        links = []
        for heading, body in sections:
            section_id = f"{chapter_id}-{slug(heading)}"
            if section_id in ids:
                raise ValueError(f"Duplicate anchor: {section_id}")
            ids.add(section_id)
            part.append(
                f'<details class="section" id="{section_id}" open>'
                f'<summary>{escape(heading)}</summary><div class="body">'
                f'{render(body)}</div></details>'
            )
            links.append(f'<li><a href="#{section_id}">{escape(heading)}</a></li>')
        part.append("</div></details>")
        chapters.append("\n".join(part))
        toc.append(
            f'<li><a href="#{chapter_id}">{escape(title)}</a>'
            f'<ol>{"".join(links)}</ol></li>'
        )
    shell, nav_count = re.subn(
        r'<nav aria-label="Chapters">.*?</nav>',
        lambda _: '<nav aria-label="Chapters"><ol>' + "".join(toc) + '</ol></nav>',
        shell, count=1, flags=re.S,
    )
    shell, main_count = re.subn(
        r'(<main>\s*<p class="lead">.*?</p>)\s*.*?(</main>)',
        lambda match: match[1] + "\n" + "\n".join(chapters) + "\n" + match[2],
        shell, count=1, flags=re.S,
    )
    if nav_count != 1 or main_count != 1:
        raise ValueError("spec.html is missing the expected navigation/content shell")
    output.write_text(shell, encoding="utf-8")
    print(f"Built {output.name}: {len(sources)} chapters, {len(ids)} anchors")


if __name__ == "__main__":
    build()
