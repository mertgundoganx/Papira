#!/usr/bin/env python3
"""Generates src/Papira/Text/UnicodeTables.cs from the Unicode Character Database.

Papira needs four properties to lay out bidirectional and cursive text: the bidirectional
class of a character, its mirrored form, the bracket it pairs with, and its Arabic joining
type. They are compiled into sorted tables the layout engine binary searches at run time.

Usage:

    mkdir ucd && cd ucd
    base=https://www.unicode.org/Public/UCD/latest/ucd
    curl -O $base/extracted/DerivedBidiClass.txt
    curl -O $base/extracted/DerivedGeneralCategory.txt
    curl -O $base/BidiMirroring.txt
    curl -O $base/BidiBrackets.txt
    curl -O $base/ArabicShaping.txt
    curl -O $base/emoji/emoji-data.txt
    cd .. && python3 tools/generate-unicode-tables.py ucd src/Papira/Text/UnicodeTables.cs
"""

import re
import sys
from pathlib import Path

MAX = 0x110000

BIDI_CLASSES = [
    "L", "R", "AL", "EN", "ES", "ET", "AN", "CS", "NSM", "BN", "B", "S", "WS", "ON",
    "LRE", "LRO", "RLE", "RLO", "PDF", "LRI", "RLI", "FSI", "PDI",
]

JOINING_TYPES = ["U", "L", "R", "D", "C", "T"]

# The @missing lines of DerivedBidiClass.txt spell the class out; the data lines abbreviate it.
LONG_NAMES = {
    "Left_To_Right": "L",
    "Right_To_Left": "R",
    "Arabic_Letter": "AL",
    "European_Number": "EN",
    "European_Terminator": "ET",
    "Arabic_Number": "AN",
    "Boundary_Neutral": "BN",
    "Other_Neutral": "ON",
}


def read_lines(path):
    for line in path.read_text(encoding="utf-8").splitlines():
        text = line.split("#")[0].strip()
        if text:
            yield text
    return


def read_missing(path):
    """The @missing lines give the property value of unassigned code points."""
    ranges = []
    for line in path.read_text(encoding="utf-8").splitlines():
        match = re.match(r"#\s*@missing:\s*([0-9A-Fa-f]+)\.\.([0-9A-Fa-f]+)\s*;\s*(\S+)", line)
        if match:
            ranges.append((int(match[1], 16), int(match[2], 16), match[3]))
    return ranges


def parse_ranges(path):
    for text in read_lines(path):
        fields = [field.strip() for field in text.split(";")]
        codes = fields[0].split("..")
        start = int(codes[0], 16)
        end = int(codes[-1], 16)
        yield start, end, fields[1:]


def bidi_classes(ucd):
    values = bytearray(MAX)
    for start, end, value in read_missing(ucd / "DerivedBidiClass.txt"):
        index = BIDI_CLASSES.index(LONG_NAMES.get(value, value))
        for code in range(start, min(end + 1, MAX)):
            values[code] = index

    for start, end, fields in parse_ranges(ucd / "DerivedBidiClass.txt"):
        index = BIDI_CLASSES.index(fields[0])
        for code in range(start, min(end + 1, MAX)):
            values[code] = index

    return values


def joining_types(ucd):
    # Characters the file does not list are transparent when they are marks or formatting
    # characters, and non-joining otherwise (ArabicShaping.txt, "Note").
    values = bytearray(MAX)
    for start, end, fields in parse_ranges(ucd / "DerivedGeneralCategory.txt"):
        if fields[0] in ("Mn", "Me", "Cf"):
            for code in range(start, min(end + 1, MAX)):
                values[code] = JOINING_TYPES.index("T")

    for start, end, fields in parse_ranges(ucd / "ArabicShaping.txt"):
        for code in range(start, min(end + 1, MAX)):
            values[code] = JOINING_TYPES.index(fields[1])

    return values


def emoji_ranges(ucd):
    """The pictographs, which shape differently from letters: a font joins a flag, a skin tone or a
    family out of several of them, and Papira has to ask it to."""
    ranges = []
    for start, end, fields in parse_ranges(ucd / "emoji-data.txt"):
        if fields[0] == "Extended_Pictographic":
            ranges.append((start, min(end, MAX - 1)))

    # Characters that are not pictographs themselves but build one with the pictograph beside them.
    # The bases of a keycap — the digits, the number sign and the asterisk — are left out on purpose:
    # they are far too common in ordinary text, and the keycap mark beside them is enough to notice one.
    ranges += [
        (0x1F1E6, 0x1F1FF),  # regional indicators, which pair into flags
        (0x1F3FB, 0x1F3FF),  # skin tone modifiers
        (0xFE0E, 0xFE0F),    # the text and emoji presentation selectors
        (0x20E3, 0x20E3),    # the enclosing keycap
        (0x200D, 0x200D),    # the zero width joiner of a sequence
    ]

    merged = []
    for start, end in sorted(ranges):
        if merged and start <= merged[-1][1] + 1:
            merged[-1][1] = max(merged[-1][1], end)
        else:
            merged.append([start, end])

    return [(start, end) for start, end in merged]


def boundaries(values, shift):
    """Turns a per-code-point table into the sorted runs the C# side binary searches."""
    packed = []
    previous = -1
    for code, value in enumerate(values):
        if value != previous:
            packed.append((code << shift) | value)
            previous = value

    return packed


def mirroring(ucd):
    pairs = [(start, int(fields[0], 16)) for start, _, fields in parse_ranges(ucd / "BidiMirroring.txt")]
    return sorted(pairs)


def brackets(ucd):
    rows = []
    for start, _, fields in parse_ranges(ucd / "BidiBrackets.txt"):
        paired = int(fields[0], 16)
        rows.append((start, paired, fields[1] == "o"))
    return sorted(rows)


def numbers(values, formatter, per_line=8):
    lines = []
    for index in range(0, len(values), per_line):
        lines.append("        " + " ".join(formatter(value) + "," for value in values[index:index + per_line]))
    return "\n".join(lines)


def main():
    ucd = Path(sys.argv[1])
    output = Path(sys.argv[2])
    version = re.search(r"-([0-9.]+)\.txt", (ucd / "DerivedBidiClass.txt").read_text(encoding="utf-8")[:200])[1]

    bidi = boundaries(bidi_classes(ucd), 5)
    joining = boundaries(joining_types(ucd), 3)
    mirrors = mirroring(ucd)
    pairs = brackets(ucd)
    emoji = emoji_ranges(ucd)

    source = f'''// Generated by tools/generate-unicode-tables.py from the Unicode Character Database {version}.
// Do not edit by hand; run the script again against a newer database instead.

namespace Papira.Text;

/// <summary>
/// The character properties bidirectional and cursive text needs. Each table is a sorted list of runs:
/// an entry holds the first code point of the run in its high bits and the property value in its low bits,
/// so a binary search finds the value of any code point.
/// </summary>
internal static class UnicodeTables
{{
    /// <summary>The version of the Unicode Character Database these tables were generated from.</summary>
    public const string UnicodeVersion = "{version}";

    /// <summary>Runs of bidirectional classes: <c>(first code point &lt;&lt; 5) | class</c>.</summary>
    public static ReadOnlySpan<int> BidiClasses =>
    [
{numbers(bidi, lambda value: f"0x{value:X}")}
    ];

    /// <summary>Runs of Arabic joining types: <c>(first code point &lt;&lt; 3) | type</c>.</summary>
    public static ReadOnlySpan<int> JoiningTypes =>
    [
{numbers(joining, lambda value: f"0x{value:X}")}
    ];

    /// <summary>Characters that have a mirrored form, sorted; <see cref="MirrorTo"/> holds the forms.</summary>
    public static ReadOnlySpan<int> MirrorFrom =>
    [
{numbers([code for code, _ in mirrors], lambda value: f"0x{value:X}")}
    ];

    public static ReadOnlySpan<int> MirrorTo =>
    [
{numbers([code for _, code in mirrors], lambda value: f"0x{value:X}")}
    ];

    /// <summary>Bracket characters, sorted. The paired bracket is at the same index in <see cref="BracketPairs"/>.</summary>
    public static ReadOnlySpan<int> BracketChars =>
    [
{numbers([code for code, _, _ in pairs], lambda value: f"0x{value:X}")}
    ];

    public static ReadOnlySpan<int> BracketPairs =>
    [
{numbers([code for _, code, _ in pairs], lambda value: f"0x{value:X}")}
    ];

    /// <summary>True where the bracket at the same index opens a pair, false where it closes one.</summary>
    public static ReadOnlySpan<bool> BracketOpens =>
    [
{numbers([opens for _, _, opens in pairs], lambda value: "true" if value else "false", 12)}
    ];

    /// <summary>
    /// The characters a font may draw as a picture, and those that build one together with the picture
    /// beside them: the first of each pair starts a range, the second ends it.
    /// </summary>
    public static ReadOnlySpan<int> EmojiRanges =>
    [
{numbers([code for pair in emoji for code in pair], lambda value: f"0x{value:X}")}
    ];
}}
'''

    output.write_text(source, encoding="utf-8")
    print(f"{output}: {len(bidi)} bidi runs, {len(joining)} joining runs, {len(mirrors)} mirrored "
          f"characters, {len(pairs)} brackets, {len(emoji)} emoji ranges, {len(source.splitlines())} lines")


if __name__ == "__main__":
    main()
