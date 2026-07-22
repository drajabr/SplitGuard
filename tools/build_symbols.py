"""Build SplitGuardSymbols.ttf: open-source Fluent System Icons glyphs remapped onto the
Segoe Fluent Icons / MDL2 codepoints SplitGuard uses, so Android renders the same XAML."""
import json, sys
from fontTools import subset
from fontTools.ttLib import TTFont

SRC = "FluentSystemIcons-Resizable.ttf"
MAP = "FluentSystemIcons-Resizable.json"
OUT = "SplitGuardSymbols.ttf"

# target Segoe codepoint -> ordered candidate Fluent icon names (first present wins)
WANT = {
    0xE70D: ["ic_fluent_chevron_down_20_regular", "ic_fluent_chevron_down_24_regular"],
    0xE70E: ["ic_fluent_chevron_up_20_regular", "ic_fluent_chevron_up_24_regular"],
    0xE70F: ["ic_fluent_edit_20_regular", "ic_fluent_edit_24_regular"],
    0xE710: ["ic_fluent_add_20_regular", "ic_fluent_add_24_regular"],
    0xE711: ["ic_fluent_dismiss_20_regular", "ic_fluent_dismiss_24_regular"],
    0xE713: ["ic_fluent_settings_20_regular", "ic_fluent_settings_24_regular"],
    0xE718: ["ic_fluent_pin_20_regular", "ic_fluent_pin_24_regular"],
    0xE71B: ["ic_fluent_link_20_regular", "ic_fluent_link_24_regular"],           # Pair (chain)
    0xE72D: ["ic_fluent_share_20_regular", "ic_fluent_share_24_regular",          # Export (share)
             "ic_fluent_share_android_24_regular"],
    0xE72C: ["ic_fluent_arrow_clockwise_20_regular", "ic_fluent_arrow_clockwise_24_regular"],
    0xE73E: ["ic_fluent_checkmark_20_regular", "ic_fluent_checkmark_24_regular"],
    0xE74A: ["ic_fluent_arrow_up_20_regular", "ic_fluent_arrow_up_24_regular"],
    0xE74B: ["ic_fluent_arrow_down_20_regular", "ic_fluent_arrow_down_24_regular"],
    0xE777: ["ic_fluent_arrow_counterclockwise_20_regular", "ic_fluent_arrow_counterclockwise_24_regular"],
    0xE783: ["ic_fluent_error_circle_20_regular", "ic_fluent_error_circle_24_regular"],
    0xE7BA: ["ic_fluent_warning_20_regular", "ic_fluent_warning_24_regular"],
    0xE80A: ["ic_fluent_form_20_regular", "ic_fluent_form_24_regular",
             "ic_fluent_document_text_20_regular", "ic_fluent_document_text_24_regular",
             "ic_fluent_textbox_20_regular", "ic_fluent_textbox_24_regular"],
    0xE842: ["ic_fluent_pin_20_filled", "ic_fluent_pin_24_filled"],
    0xE895: ["ic_fluent_arrow_sync_20_regular", "ic_fluent_arrow_sync_24_regular"],
    0xE896: ["ic_fluent_arrow_download_20_regular", "ic_fluent_arrow_download_24_regular"],
    0xE8B5: ["ic_fluent_arrow_download_20_regular", "ic_fluent_arrow_download_24_regular"],
    0xE8C8: ["ic_fluent_copy_20_regular", "ic_fluent_copy_24_regular"],
    0xE8B8: ["ic_fluent_qr_code_20_regular", "ic_fluent_qr_code_24_regular"],
    # Scan affordances (peer scan button, Add-drawer scan row, scan pane titles): the
    # 4-corner scan frame (Segoe GenericScan), not the QR grid.
    0xEE6F: ["ic_fluent_scan_dash_20_regular", "ic_fluent_scan_dash_24_regular",
             "ic_fluent_scan_20_regular", "ic_fluent_scan_24_regular"],
}

names = json.load(open(MAP, encoding="utf-8"))  # name -> source codepoint
resolved = {}
for target, candidates in WANT.items():
    for c in candidates:
        if c in names:
            resolved[target] = names[c]
            break
    else:
        sys.exit(f"no candidate found for U+{target:04X}: {candidates}")

# subset to just the source codepoints we need
src_cps = sorted(set(resolved.values()))
opts = subset.Options()
opts.set(layout_features="*", glyph_names=True, notdef_outline=True)
ss = subset.Subsetter(options=opts)
font = TTFont(SRC)
ss.populate(unicodes=src_cps)
ss.subset(font)

# remap cmap: source cp -> glyph, re-keyed at target cp
cmap_table = font.getBestCmap()
new_map = {}
for target, src in resolved.items():
    g = cmap_table.get(src)
    if g is None:
        sys.exit(f"glyph missing after subset for source U+{src:04X}")
    new_map[target] = g
for table in font["cmap"].tables:
    if table.isUnicode():
        table.cmap = dict(new_map)

# rename family
FAMILY = "SplitGuard Symbols"
for rec in font["name"].names:
    if rec.nameID in (1, 3, 4, 6, 16):
        val = FAMILY if rec.nameID in (1, 16) else (
            FAMILY if rec.nameID == 4 else FAMILY.replace(" ", ""))
        rec.string = val.encode("utf-16-be") if b"\x00" in rec.toBytes() else val.encode("latin-1")

font.save(OUT)
print("wrote", OUT, "with", len(new_map), "glyphs:",
      ", ".join(f"U+{k:04X}" for k in sorted(new_map)))
