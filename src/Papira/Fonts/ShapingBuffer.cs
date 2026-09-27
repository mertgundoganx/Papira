using Papira.Text;

namespace Papira.Fonts;

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

    public Span<ShapedGlyph> Glyphs => _glyphs.AsSpan(0, Length);

    public ref ShapedGlyph this[int index] => ref _glyphs[index];

    public void Clear() => Length = 0;

    public void Add(ushort glyph, int cluster, JoiningForm form, bool invisible = false)
    {
        if (Length == _glyphs.Length)
            Array.Resize(ref _glyphs, _glyphs.Length * 2);

        _glyphs[Length++] = new ShapedGlyph { Glyph = glyph, Cluster = cluster, Length = 1, Form = form, Invisible = invisible };
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
    public void Replace(int index, ushort glyph) => _glyphs[index].Glyph = glyph;

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
