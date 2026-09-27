using Papira.Text;

namespace Papira.Fonts;

/// <summary>A zero width joiner asks letters to join, a non-joiner asks them not to.</summary>
internal enum JoinerKind : byte { None = 0, Joiner = 1, NonJoiner = 2 }

/// <summary>What a glyph is fastened to, once the font's positioning rules have run.</summary>
internal enum AttachmentKind : byte { None = 0, Mark = 1, Cursive = 2 }

/// <summary>One position of a run being shaped.</summary>
internal struct ShapedGlyph
{
    public ushort Glyph;

    /// <summary>The character of the run this glyph came from; several glyphs may share one character.</summary>
    public int Cluster;

    /// <summary>How many characters of the run this glyph covers, which a ligature makes larger than one.</summary>
    public ushort Length;

    public JoiningForm Form;

    /// <summary>A character that shapes its neighbours but is never drawn, such as a zero width joiner.</summary>
    public bool Invisible;

    /// <summary>True for a glyph the font draws on the one before it, such as an accent.</summary>
    public bool Mark;

    /// <summary>What kind of joiner this glyph is, if it is one: they shape their neighbours and are never drawn.</summary>
    public JoinerKind JoinerKind;

    public readonly bool Joiner => JoinerKind != JoinerKind.None;

    /// <summary>The advance the font gives the glyph, before positioning changes it. In font units.</summary>
    public short BaseAdvance;

    /// <summary>How far the glyph moves from where the pen stands, in font units.</summary>
    public short XOffset;

    public short YOffset;

    /// <summary>What positioning adds to the advance of the glyph, in font units.</summary>
    public short XAdvance;

    /// <summary>What the glyph hangs on, and how far away that glyph is in the buffer.</summary>
    public AttachmentKind AttachType;

    public short AttachChain;

    /// <summary>What kind of character this is in an Indic syllable, and where in it the glyph goes.</summary>
    public byte Category;

    public byte Position;

    /// <summary>The features that apply to this glyph, one bit each; used by the Indic scripts.</summary>
    public uint Features;

    /// <summary>True once a rule has put this glyph in the place of another.</summary>
    public bool Substituted;

    /// <summary>True when this glyph came out of several, as a ligature does.</summary>
    public bool Ligated;

    /// <summary>True when this glyph is one of several a rule made out of one.</summary>
    public bool Multiplied;

    /// <summary>A ligature that did not then become several glyphs, which is what reordering asks about.</summary>
    public readonly bool LigatedAlone => Ligated && !Multiplied;
}

/// <summary>
/// The glyphs of one run while the font's substitution rules are applied to them. A rule can replace a
/// glyph, expand it into several or merge several into one, so positions and characters stop matching
/// one to one; the cluster of each glyph records where it came from.
/// </summary>
internal sealed class ShapingBuffer
{
    private ShapedGlyph[] _glyphs = new ShapedGlyph[16];

    public int Length { get; private set; }

    /// <summary>
    /// The features the rules being applied belong to. A glyph only takes part in a rule when it
    /// carries one of them; zero means every glyph does.
    /// </summary>
    public uint ActiveMask { get; set; }

    /// <summary>
    /// Whether the rules being applied step over the joiners. A joiner is always stepped over when a
    /// rule looks at the context around a glyph; whether it is stepped over inside what the rule
    /// replaces, and whether a non-joiner is stepped over at all, depends on the feature: the ones that
    /// join or reorder letters see them, the ones every script gets do not.
    /// </summary>
    public bool StepOverJoiners { get; set; } = true;

    public Span<ShapedGlyph> Glyphs => _glyphs.AsSpan(0, Length);

    public ref ShapedGlyph this[int index] => ref _glyphs[index];

    public void Clear() => Length = 0;

    public void Add(ushort glyph, int cluster, JoiningForm form, bool invisible = false)
    {
        if (Length == _glyphs.Length)
            Array.Resize(ref _glyphs, _glyphs.Length * 2);

        _glyphs[Length++] = new ShapedGlyph { Glyph = glyph, Cluster = cluster, Length = 1, Form = form, Invisible = invisible };
    }

    /// <summary>Adds a glyph that another buffer already shaped, keeping everything known about it.</summary>
    public void Add(in ShapedGlyph glyph)
    {
        if (Length == _glyphs.Length)
            Array.Resize(ref _glyphs, _glyphs.Length * 2);

        _glyphs[Length++] = glyph;
    }

    /// <summary>Puts a glyph in front of the one at <paramref name="index"/>, shifting the rest along.</summary>
    public void Insert(int index, ushort glyph, int cluster)
    {
        if (Length == _glyphs.Length)
            Array.Resize(ref _glyphs, _glyphs.Length * 2);

        Array.Copy(_glyphs, index, _glyphs, index + 1, Length - index);
        _glyphs[index] = new ShapedGlyph { Glyph = glyph, Cluster = cluster, Length = 1, Form = JoiningForm.Isolated };
        Length++;
    }

    /// <summary>Moves the glyph at <paramref name="from"/> to <paramref name="to"/>, shifting the rest.</summary>
    public void Move(int from, int to)
    {
        if (from == to)
            return;

        var moved = _glyphs[from];
        if (from < to)
            Array.Copy(_glyphs, from + 1, _glyphs, from, to - from);
        else
            Array.Copy(_glyphs, to, _glyphs, to + 1, from - to);

        _glyphs[to] = moved;
    }

    /// <summary>Sorts a stretch of the buffer by where each glyph belongs, keeping equal ones in order.</summary>
    public void StableSortByPosition(int start, int end)
    {
        // Insertion sort: a syllable is a handful of glyphs, and equal positions keep their order.
        for (var i = start + 1; i < end; i++)
        {
            var current = _glyphs[i];
            var j = i - 1;
            while (j >= start && _glyphs[j].Position > current.Position)
            {
                _glyphs[j + 1] = _glyphs[j];
                j--;
            }

            _glyphs[j + 1] = current;
        }
    }

    /// <summary>Turns a stretch of the buffer back to front, as a run of left matras needs.</summary>
    public void Reverse(int start, int end)
    {
        for (int i = start, j = end - 1; i < j; i++, j--)
            (_glyphs[i], _glyphs[j]) = (_glyphs[j], _glyphs[i]);
    }

    /// <summary>
    /// Drops the characters that were only there to shape their neighbours. They take part in the
    /// substitution rules — a joiner between two letters keeps them from forming a ligature — and are
    /// removed once those rules have run.
    /// </summary>
    public void RemoveInvisible()
    {
        var kept = 0;
        for (var i = 0; i < Length; i++)
        {
            if (!_glyphs[i].Invisible)
                _glyphs[kept++] = _glyphs[i];
        }

        Length = kept;
    }

    /// <summary>Replaces one glyph with another, keeping its cluster.</summary>
    public void Replace(int index, ushort glyph)
    {
        _glyphs[index].Glyph = glyph;
        _glyphs[index].Substituted = true;
    }

    /// <summary>
    /// Replaces the glyphs at <paramref name="positions"/> with one, as a ligature does. The first position
    /// becomes the ligature; anything the lookup skipped between them — a mark, say — stays in the buffer
    /// and ends up after it.
    /// </summary>
    public void Merge(ReadOnlySpan<int> positions, ushort glyph)
    {
        var length = 0;
        foreach (var position in positions)
            length += _glyphs[position].Length;

        var first = positions[0];
        _glyphs[first].Glyph = glyph;
        _glyphs[first].Length = (ushort)Math.Min(length, ushort.MaxValue);
        _glyphs[first].Substituted = true;
        _glyphs[first].Ligated = true;

        var write = first + 1;
        var next = 1;
        for (var read = first + 1; read < Length; read++)
        {
            if (next < positions.Length && read == positions[next])
            {
                next++;
                continue;
            }

            _glyphs[write++] = _glyphs[read];
        }

        Length = write;
    }

    /// <summary>Replaces one glyph with several, which all share its cluster.</summary>
    public void Expand(int index, ReadOnlySpan<ushort> glyphs)
    {
        if (glyphs.Length == 0)
        {
            Remove(index, 1);
            return;
        }

        var extra = glyphs.Length - 1;
        if (Length + extra > _glyphs.Length)
            Array.Resize(ref _glyphs, Math.Max(_glyphs.Length * 2, Length + extra));

        Array.Copy(_glyphs, index + 1, _glyphs, index + 1 + extra, Length - index - 1);
        Length += extra;

        var template = _glyphs[index];
        template.Substituted = true;
        template.Multiplied = glyphs.Length > 1;
        for (var i = 0; i < glyphs.Length; i++)
        {
            _glyphs[index + i] = template;
            _glyphs[index + i].Glyph = glyphs[i];

            // Only the first glyph carries the characters; the rest are part of the same cluster.
            _glyphs[index + i].Length = i == 0 ? template.Length : (ushort)0;
        }
    }

    private void Remove(int index, int count)
    {
        Array.Copy(_glyphs, index + count, _glyphs, index, Length - index - count);
        Length -= count;
    }
}
