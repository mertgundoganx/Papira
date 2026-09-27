#!/usr/bin/env python3
"""Builds src/Papira/Text/IndicTables.cs from the Unicode Character Database.

Every character of an Indic script is given the two properties the shaper needs: what kind of
character it is (a consonant, a vowel sign, a virama...) and where it goes in the syllable. Both
follow from Indic_Syllabic_Category, Indic_Positional_Category and the block the character is in,
the way the OpenType Indic script development specification describes.

Usage:
    python3 tools/generate-indic-tables.py path/to/ucd > src/Papira/Text/IndicTables.cs

where the folder holds IndicSyllabicCategory.txt, IndicPositionalCategory.txt and Blocks.txt from
https://www.unicode.org/Public/UCD/latest/ucd/.
"""
import sys
from pathlib import Path

BLOCKS = [
    'Basic Latin', 'Latin-1 Supplement', 'Devanagari', 'Bengali', 'Gurmukhi', 'Gujarati', 'Oriya',
    'Tamil', 'Telugu', 'Kannada', 'Malayalam', 'Vedic Extensions', 'General Punctuation',
    'Superscripts and Subscripts', 'Devanagari Extended', 'Grantha',
]
SINGLES = [0x00A0, 0x25CC]

CATEGORY = {
    'Other': 'X', 'Avagraha': 'Symbol', 'Bindu': 'SM', 'Brahmi_Joining_Number': 'Placeholder',
    'Cantillation_Mark': 'A', 'Consonant': 'C', 'Consonant_Dead': 'C', 'Consonant_Final': 'CM',
    'Consonant_Head_Letter': 'C', 'Consonant_Initial_Postfixed': 'C', 'Consonant_Killer': 'M',
    'Consonant_Medial': 'CM', 'Consonant_Placeholder': 'Placeholder',
    'Consonant_Preceding_Repha': 'Repha', 'Consonant_Prefixed': 'X', 'Consonant_Subjoined': 'CM',
    'Consonant_Succeeding_Repha': 'CM', 'Consonant_With_Stacker': 'CS', 'Gemination_Mark': 'SM',
    'Invisible_Stacker': 'H', 'Joiner': 'ZWJ', 'Modifying_Letter': 'X', 'Non_Joiner': 'ZWNJ',
    'Nukta': 'N', 'Number': 'Placeholder', 'Number_Joiner': 'Placeholder', 'Pure_Killer': 'M',
    'Register_Shifter': 'RS', 'Syllable_Modifier': 'SM', 'Tone_Letter': 'X', 'Tone_Mark': 'N',
    'Virama': 'H', 'Visarga': 'SM', 'Vowel': 'V', 'Vowel_Dependent': 'M', 'Vowel_Independent': 'V',
}

POSITION = {
    'Not_Applicable': 'End',
    'Left': 'PreC', 'Top': 'AboveC', 'Bottom': 'BelowC', 'Right': 'PostC',
    'Bottom_And_Right': 'PostC', 'Left_And_Right': 'PostC', 'Top_And_Bottom': 'BelowC',
    'Top_And_Bottom_And_Left': 'BelowC', 'Top_And_Bottom_And_Right': 'PostC',
    'Top_And_Left': 'AboveC', 'Top_And_Left_And_Right': 'PostC', 'Top_And_Right': 'PostC',
    'Overstruck': 'AfterMain', 'Visual_Order_Left': 'PreM',
}

CATEGORY_OVERRIDES = {
    # The letter Ra of each script, which forms the reph.
    0x0930: 'Ra', 0x09B0: 'Ra', 0x09F0: 'Ra', 0x0A30: 'Ra', 0x0AB0: 'Ra', 0x0B30: 'Ra',
    0x0BB0: 'Ra', 0x0C30: 'Ra', 0x0CB0: 'Ra', 0x0D30: 'Ra',
    # These act like the bindus.
    0x0953: 'SM', 0x0954: 'SM',
    # This vowel sign may be preceded by a bindu, so it is a post-base matra.
    0x0A40: 'MPst',
    # These act like consonants.
    0x0A72: 'C', 0x0A73: 'C',
    # Treated like tone marks until the sequences they belong to are understood.
    0x1CE2: 'A', 0x1CE3: 'A', 0x1CE4: 'A', 0x1CE5: 'A', 0x1CE6: 'A', 0x1CE7: 'A', 0x1CE8: 'A',
    0x1CED: 'A',
    # These take marks in clusters of their own, as an avagraha does.
    0xA8F2: 'Symbol', 0xA8F3: 'Symbol', 0xA8F4: 'Symbol', 0xA8F5: 'Symbol', 0xA8F6: 'Symbol',
    0xA8F7: 'Symbol', 0x1CE9: 'Symbol', 0x1CEA: 'Symbol', 0x1CEB: 'Symbol', 0x1CEC: 'Symbol',
    0x1CEE: 'Symbol', 0x1CEF: 'Symbol', 0x1CF0: 'Symbol', 0x1CF1: 'Symbol',
    0x0A51: 'M',
    # Grantha marks, which Tamil uses as well.
    0x11301: 'SM', 0x11302: 'SM', 0x11303: 'SM', 0x1133B: 'N', 0x1133C: 'N',
    0x0AFB: 'N', 0x0B55: 'N',
    0x09FC: 'Placeholder', 0x0C80: 'Placeholder', 0x0D04: 'Placeholder',
    0x25CC: 'DottedCircle',
}

POSITION_OVERRIDES = {
    0x0A51: 'BelowC',
    0x0B01: 'BeforeSub',   # the Oriya bindu comes before the subjoined forms
}

CONSONANTS = ('C', 'CS', 'Ra', 'CM', 'V', 'Placeholder', 'DottedCircle')
MATRAS = ('M', 'MPst')
SMVD = ('SM', 'SMPst', 'A', 'Symbol')
POSITIONED = ('CM', 'SM', 'RS', 'H', 'M', 'MPst')


def matra_right(u, block):
    return {
        'Devanagari': 'AfterSub', 'Bengali': 'AfterPost', 'Gurmukhi': 'AfterPost',
        'Gujarati': 'AfterPost', 'Oriya': 'AfterPost', 'Tamil': 'AfterPost',
        'Telugu': 'BeforeSub' if u <= 0x0C42 else 'AfterSub',
        'Kannada': 'BeforeSub' if u < 0x0CC3 or u > 0x0CD6 else 'AfterSub',
        'Malayalam': 'AfterPost',
    }.get(block, 'AfterSub')


def matra_top(u, block):
    return {
        'Devanagari': 'AfterSub', 'Gurmukhi': 'AfterPost', 'Gujarati': 'AfterSub',
        'Oriya': 'AfterMain', 'Tamil': 'AfterSub', 'Telugu': 'BeforeSub', 'Kannada': 'BeforeSub',
    }.get(block, 'AfterSub')


def matra_bottom(u, block):
    return {
        'Devanagari': 'AfterSub', 'Bengali': 'AfterSub', 'Gurmukhi': 'AfterPost',
        'Gujarati': 'AfterPost', 'Oriya': 'AfterSub', 'Tamil': 'AfterPost',
        'Telugu': 'BeforeSub', 'Kannada': 'BeforeSub', 'Malayalam': 'AfterPost',
    }.get(block, 'AfterSub')


def matra_position(u, pos, block):
    if pos == 'PreC':
        return 'PreM'
    if pos == 'PostC':
        return matra_right(u, block)
    if pos == 'AboveC':
        return matra_top(u, block)
    if pos == 'BelowC':
        return matra_bottom(u, block)
    return pos


def read(path):
    data = {}
    for line in open(path, encoding='utf-8'):
        line = line.split('#')[0].strip()
        if not line:
            continue
        fields = [f.strip() for f in line.split(';')]
        if len(fields) < 2:
            continue
        span = fields[0].split('..')
        start = int(span[0], 16)
        end = int(span[1], 16) if len(span) > 1 else start
        for u in range(start, end + 1):
            data[u] = fields[1]
    return data


def main(folder):
    folder = Path(folder)
    syllabic = read(folder / 'IndicSyllabicCategory.txt')
    positional = read(folder / 'IndicPositionalCategory.txt')
    blocks = read(folder / 'Blocks.txt')

    wanted = {u for u, block in blocks.items() if block in BLOCKS} | set(SINGLES)
    table = {}
    for u in sorted(wanted):
        block = blocks.get(u, 'No_Block')
        category = CATEGORY[syllabic.get(u, 'Other')]
        position = positional.get(u, 'Not_Applicable')
        if category == 'SM' and position == 'Not_Applicable':
            category = 'SMPst'

        position = POSITION[position]
        if u in CATEGORY_OVERRIDES:
            category = CATEGORY_OVERRIDES[u]

        if category not in POSITIONED:
            position = 'End'

        if category in CONSONANTS:
            position = 'BaseC'
        elif category in MATRAS:
            position = matra_position(u, position, block)
        elif category in SMVD:
            position = 'Smvd'

        if u in POSITION_OVERRIDES:
            position = POSITION_OVERRIDES[u]

        if category != 'X' or position != 'End':
            table[u] = (category, position)

    # Runs of characters that share both properties become one entry.
    ranges = []
    for u in sorted(table):
        category, position = table[u]
        if ranges and ranges[-1][1] == u - 1 and ranges[-1][2] == category and ranges[-1][3] == position:
            ranges[-1][1] = u
        else:
            ranges.append([u, u, category, position])

    print('// Generated by tools/generate-indic-tables.py from the Unicode Character Database.')
    print('// Do not edit by hand.')
    print()
    print('namespace Papira.Text;')
    print()
    print('/// <summary>What each character of an Indic script is, and where it goes in its syllable.</summary>')
    print('internal static class IndicTables')
    print('{')
    print('    /// <summary>First character of each run, in order; the runs cover the Indic scripts.</summary>')
    print('    public static ReadOnlySpan<int> Starts =>')
    print('    [')
    for i in range(0, len(ranges), 12):
        print('        ' + ' '.join(f'0x{r[0]:04X},' for r in ranges[i:i + 12]))
    print('    ];')
    print()
    print('    /// <summary>Last character of each run.</summary>')
    print('    public static ReadOnlySpan<int> Ends =>')
    print('    [')
    for i in range(0, len(ranges), 12):
        print('        ' + ' '.join(f'0x{r[1]:04X},' for r in ranges[i:i + 12]))
    print('    ];')
    print()
    print('    /// <summary>The kind of character each run holds.</summary>')
    print('    public static ReadOnlySpan<byte> Categories =>')
    print('    [')
    for i in range(0, len(ranges), 8):
        print('        ' + ' '.join(f'(byte)IndicCategory.{r[2]},' for r in ranges[i:i + 8]))
    print('    ];')
    print()
    print('    /// <summary>Where in the syllable each run belongs.</summary>')
    print('    public static ReadOnlySpan<byte> Positions =>')
    print('    [')
    for i in range(0, len(ranges), 8):
        print('        ' + ' '.join(f'(byte)IndicPosition.{r[3]},' for r in ranges[i:i + 8]))
    print('    ];')
    print('}')
    print(f'// {len(ranges)} runs, {len(table)} characters', file=sys.stderr)


if __name__ == '__main__':
    main(sys.argv[1] if len(sys.argv) > 1 else 'ucd')
