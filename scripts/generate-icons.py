#!/usr/bin/env python3
"""Normalise the icon SVGs in assets/icons/svg and emit the Blazor icon library.

Two things in the source set do not survive being inlined into a shared document, and
both are fixed here rather than in the artwork so the SVGs stay the design source:

  * Seven icons declare their gradient as `id="g"`. SVG ids are document-global, so once
    two of them render together every `url(#g)` resolves to whichever landed first and
    the icons silently take each other's colours. Ids are rewritten per icon.

  * Twenty-four icons hard-code their stroke, so they cannot respond to hover, disabled,
    selected, or a context that needs them to match its text. The stroke becomes
    currentColor and the artwork tone moves to a generated CSS rule, which renders
    identically but can be overridden by any ordinary rule.

Run: python3 scripts/generate-icons.py
"""
import io, os, re, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SVG_DIR = os.path.join(ROOT, "assets", "icons", "svg")
CS_OUT = os.path.join(
    ROOT, "src", "extensions",
    "BotNexus.Extensions.Channels.SignalR.BlazorClient.Core",
    "Components", "IconLibrary.g.cs")
CSS_OUT = os.path.join(
    ROOT, "src", "extensions",
    "BotNexus.Extensions.Channels.SignalR.BlazorClient.Core",
    "wwwroot", "css", "tokens.css")

# The artwork's hues, mapped to the tokens declared in tokens.css. Emitting a raw hex
# here is what made the icon tones the last unthemeable colour in the portal, so an
# artwork colour that is NOT in this map is a hard error rather than a silent
# passthrough: add a token to tokens.css (both themes) and a line here.
TONES = {
    "#22C55E": "green",  "#3B82F6": "blue",   "#8B5CF6": "purple",
    "#EF4444": "red",    "#06B6D4": "cyan",   "#F59E0B": "amber",
    "#6366F1": "indigo", "#14B8A6": "teal",
}


def tone_token(name, value):
    """Map an artwork colour onto its --icon-tone-* token."""
    key = value.strip().upper()
    if key not in TONES:
        raise SystemExit(
            "%s: tone %s has no --icon-tone-* token.\n"
            "Add it to tokens.css under BOTH :root and [data-theme=\"light\"], then to\n"
            "TONES in this script. Icon tones must not be raw hex - they cannot theme."
            % (name, value))
    return "var(--icon-tone-%s)" % TONES[key]


CSS_BEGIN = "/* BEGIN generated icon tones -- scripts/generate-icons.py */"
CSS_END = "/* END generated icon tones */"


def pascal(name):
    return "".join(p.capitalize() for p in re.split(r"[-_]", name))


def parse(path, name):
    raw = io.open(path, encoding="utf-8").read().strip()
    m = re.match(r"<svg\b([^>]*)>(.*)</svg>\s*$", raw, re.S)
    if not m:
        raise SystemExit("%s: not a single <svg> element" % name)
    attrs, body = m.group(1), m.group(2).strip()

    sm = re.search(r'\bstroke="([^"]*)"', attrs)
    if not sm:
        raise SystemExit("%s: no stroke on the root element" % name)
    stroke = sm.group(1)

    # Give every gradient an id unique to its icon, and repoint the references.
    ids = re.findall(r'<(?:linear|radial)Gradient\b[^>]*\bid="([^"]+)"', body)
    tone = None
    for gid in ids:
        unique = "bn-%s-%s" % (name, gid)
        body = re.sub(r'(<(?:linear|radial)Gradient\b[^>]*\bid=")%s(")' % re.escape(gid),
                      r"\g<1>%s\g<2>" % unique, body)
        body = body.replace("url(#%s)" % gid, "url(#%s)" % unique)
        stroke = stroke.replace("url(#%s)" % gid, "url(#%s)" % unique)

    if stroke.startswith("url("):
        # A gradient carries no single colour. Keep the first stop as the tone so a
        # context that forces the icon flat still gets something from the same family.
        stop = re.search(r'stop-color="([^"]+)"', body)
        tone = stop.group(1) if stop else None
    elif stroke.lower() != "currentcolor":
        tone, stroke = stroke, "currentColor"

    return stroke, tone, body


def main():
    # "._name.svg" is an AppleDouble sidecar, not artwork: macOS writes one per file on
    # filesystems without native resource-fork support (exFAT, NTFS, many network shares).
    # They are untracked junk, but os.listdir sees them and they are not valid UTF-8, so
    # without this the script dies on a decode error rather than on anything real.
    names = sorted(f[:-4] for f in os.listdir(SVG_DIR)
                   if f.endswith(".svg") and not f.startswith("._"))
    if not names:
        raise SystemExit("no SVGs in %s" % SVG_DIR)

    parsed = [(n,) + parse(os.path.join(SVG_DIR, n + ".svg"), n) for n in names]

    # Every generated id must be unique across the whole set, or the collision we are
    # fixing simply moves. Assert it rather than trusting the naming scheme.
    seen = {}
    for name, _stroke, _tone, body in parsed:
        for gid in re.findall(r'\bid="([^"]+)"', body):
            if gid in seen:
                raise SystemExit("id %r emitted by both %s and %s" % (gid, seen[gid], name))
            seen[gid] = name

    cs = [
        "// <auto-generated />",
        "// Generated by scripts/generate-icons.py from assets/icons/svg. Do not edit by hand:",
        "// change the SVG and re-run the script. See the script header for what it normalises.",
        "",
        # RootNamespace on the Core project drops the .Core segment, so components there
        # land in ...BlazorClient.Components alongside the desktop ones.
        "namespace BotNexus.Extensions.Channels.SignalR.BlazorClient.Components;",
        "",
        "/// <summary>The portal icon set, inlined so each icon can inherit and override colour.</summary>",
        "public static class IconLibrary",
        "{",
        "    /// <param name=\"Stroke\">Root stroke: currentColor, or url(#...) for a gradient icon.</param>",
        "    /// <param name=\"Body\">Inner markup, with gradient ids already made unique.</param>",
        "    public readonly record struct IconDefinition(string Stroke, string Body);",
        "",
        "    public static readonly IReadOnlyDictionary<string, IconDefinition> Icons =",
        "        new Dictionary<string, IconDefinition>(StringComparer.OrdinalIgnoreCase)",
        "        {",
    ]
    for name, stroke, _tone, body in parsed:
        if '"""' in body:
            raise SystemExit("%s: body contains a raw-string terminator" % name)
        cs.append('            ["%s"] = new("%s",' % (name, stroke))
        cs.append('                """')
        cs.append("                " + body)
        cs.append('                """),')
    cs += [
        "        };",
        "",
        "    /// <summary>Every icon name in the set, for tests and tooling.</summary>",
        "    public static IReadOnlyList<string> Names { get; } =",
        "    [",
    ]
    cs += ['        "%s",' % n for n, _s, _t, _b in parsed]
    cs += ["    ];", "}", ""]
    io.open(CS_OUT, "w", encoding="utf-8").write("\n".join(cs))

    css = [CSS_BEGIN,
           "/* The tone each icon was drawn in, as a token reference so it themes and so the",
           "   value is declared once. Kept on a class rather than on the element so any",
           "   context can override it -- a disabled control, a selected nav row, a button",
           "   that needs the icon to match its label. */"]
    for name, _stroke, tone, _body in parsed:
        if tone:
            css.append(".bn-icon-%s { color: %s; }" % (name, tone_token(name, tone)))

    # These have to be emitted *after* the per-icon tones. They are single-class selectors
    # like the tones are, so ordering is the only thing that decides which wins; written
    # above the generated block they lost to .bn-icon-<name> and did nothing.
    css += [
        "",
        "/* Drop a gradient for the icon\'s flat tone, for a context with its own colour.",
        "   Beats the root\'s stroke=\"url(#...)\" because any declaration outranks a",
        "   presentation attribute, and the children inherit stroke from the root. */",
        ".bn-icon-flat { stroke: currentColor; }",
        "",
        "/* Where an icon must read as part of its label rather than as its own object. */",
        ".bn-icon-inherit { color: inherit; stroke: currentColor; }",
    ]
    css.append(CSS_END)
    css_text = "\n".join(css)

    existing = io.open(CSS_OUT, encoding="utf-8").read()
    if CSS_BEGIN in existing:
        existing = re.sub(re.escape(CSS_BEGIN) + r".*?" + re.escape(CSS_END),
                          lambda _m: css_text, existing, flags=re.S)
    else:
        existing = existing.rstrip("\n") + "\n\n" + css_text + "\n"
    io.open(CSS_OUT, "w", encoding="utf-8").write(existing)

    grad = sum(1 for _n, s, _t, _b in parsed if s.startswith("url("))
    print("%d icons: %d gradient, %d toned, %d inherit"
          % (len(parsed), grad,
             sum(1 for _n, _s, t, _b in parsed if t and not _s.startswith("url(")),
             sum(1 for _n, _s, t, _b in parsed if t is None)))


if __name__ == "__main__":
    main()
