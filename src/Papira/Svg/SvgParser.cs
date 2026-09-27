using System.Xml;
using Papira.Infrastructure;
using Papira.Rendering;

namespace Papira.Svg;

/// <summary>An element of the XML tree; it lives only while the file is being parsed.</summary>
internal sealed class SvgXmlElement(string name)
{
    public string Name { get; } = name;

    public Dictionary<string, string> Attributes { get; } = new(StringComparer.Ordinal);

    public List<SvgXmlElement> Children { get; } = [];

    /// <summary>The text of a &lt;style&gt; element; no other text is kept.</summary>
    public string? Text { get; set; }

    public string? Attribute(string name) => Attributes.GetValueOrDefault(name);
}

/// <summary>
/// Turns SVG markup into a tree of drawable nodes. Everything that cannot be drawn with the PDF graphics
/// operators Papira emits — text, raster images, filters, patterns, masks — is skipped rather than approximated.
/// The limits keep a hostile file from exhausting memory, in the same way the image decoders do.
/// </summary>
internal sealed class SvgParser
{
    private const int MaxElements = 200_000;
    private const int MaxNodes = 100_000;
    private const int MaxPathCommands = 200_000;
    private const int MaxDepth = 64;
    private const int MaxReferenceDepth = 8;
    private const int MaxStops = 256;

    private readonly Dictionary<string, SvgXmlElement> _byId = new(StringComparer.Ordinal);
    private readonly StyleSheet _styles = new();
    private float _viewportWidth;
    private float _viewportHeight;
    private int _nodes;

    /// <summary>
    /// Reads a drawing. <paramref name="elementId"/> takes only the element of that name and what it
    /// contains, which is how a font stores one glyph inside a document that holds several.
    /// </summary>
    public static SvgDocument Parse(byte[] data, string? elementId = null)
    {
        var root = ReadXml(data);
        if (!string.Equals(root.Name, "svg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The root element of an SVG document must be <svg>, not <{root.Name}>.");

        var parser = new SvgParser();
        parser.Index(root);
        return parser.Build(root, elementId);
    }

    // ---- XML ------------------------------------------------------------------------------------

    private static SvgXmlElement ReadXml(byte[] data)
    {
        var settings = new XmlReaderSettings
        {
            // A DOCTYPE is skipped instead of processed, so no external or internal entity is ever resolved.
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
        };

        using var stream = new MemoryStream(data, false);
        using var reader = XmlReader.Create(stream, settings);

        SvgXmlElement? root = null;
        var open = new Stack<SvgXmlElement>();
        var elements = 0;

        try
        {
            while (reader.Read())
            {
                switch (reader.NodeType)
                {
                    case XmlNodeType.Element:
                        if (++elements > MaxElements)
                            throw new InvalidDataException($"The SVG document has more than {MaxElements} elements.");

                        var element = new SvgXmlElement(reader.LocalName);
                        if (reader.HasAttributes)
                        {
                            // Local names collapse xlink:href onto href, which is what SVG 2 renamed it to.
                            while (reader.MoveToNextAttribute())
                                element.Attributes[reader.LocalName] = reader.Value;

                            reader.MoveToElement();
                        }

                        if (open.Count > 0)
                            open.Peek().Children.Add(element);
                        else
                            root ??= element;

                        if (!reader.IsEmptyElement)
                        {
                            if (open.Count >= MaxDepth)
                                throw new InvalidDataException($"The SVG document nests deeper than {MaxDepth} elements.");

                            open.Push(element);
                        }

                        break;

                    case XmlNodeType.Text or XmlNodeType.CDATA:
                        // Only style sheets need their text; keeping the rest would just cost memory.
                        if (open.Count > 0 && open.Peek().Name == "style")
                            open.Peek().Text += reader.Value;

                        break;

                    case XmlNodeType.EndElement:
                        if (open.Count > 0)
                            open.Pop();

                        break;
                }
            }
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException("The SVG markup is not well-formed XML.", exception);
        }

        return root ?? throw new InvalidDataException("The SVG document is empty.");
    }

    private void Index(SvgXmlElement element)
    {
        if (element.Attribute("id") is { Length: > 0 } id)
            _byId.TryAdd(id, element);

        if (element.Name == "style" && element.Text is { Length: > 0 } css)
            _styles.Add(css);

        foreach (var child in element.Children)
            Index(child);
    }

    // ---- Document -------------------------------------------------------------------------------

    private SvgDocument Build(SvgXmlElement root, string? elementId = null)
    {
        var viewBox = Numbers(root.Attribute("viewBox"));
        float x = 0, y = 0, width = 0, height = 0;
        if (viewBox.Length >= 4 && viewBox[2] > 0 && viewBox[3] > 0)
            (x, y, width, height) = (viewBox[0], viewBox[1], viewBox[2], viewBox[3]);

        // The intrinsic size: the width and height attributes, else the viewBox, else the CSS default.
        var intrinsicWidth = SvgValues.Length(root.Attribute("width"), width, width);
        var intrinsicHeight = SvgValues.Length(root.Attribute("height"), height, height);
        if (intrinsicWidth <= 0)
            intrinsicWidth = width > 0 ? width : 300;

        if (intrinsicHeight <= 0)
            intrinsicHeight = height > 0 ? height : 150;

        if (width <= 0 || height <= 0)
            (x, y, width, height) = (0, 0, intrinsicWidth, intrinsicHeight);

        _viewportWidth = width;
        _viewportHeight = height;

        // The <svg> element itself can carry presentation attributes that its children inherit.
        var group = new SvgGroupNode();
        var style = ResolveStyle(root, Declarations(root), SvgStyle.Initial);
        if (elementId != null)
        {
            if (_byId.TryGetValue(elementId, out var element))
                Append(group, element, style, 0, 0);
        }
        else
        {
            foreach (var child in root.Children)
                Append(group, child, style, 0, 0);
        }

        return new SvgDocument
        {
            Root = group,
            Width = intrinsicWidth,
            Height = intrinsicHeight,
            ViewBoxX = x,
            ViewBoxY = y,
            ViewBoxWidth = width,
            ViewBoxHeight = height,
            PreserveAspectRatio = root.Attribute("preserveAspectRatio")?.Contains("none", StringComparison.OrdinalIgnoreCase) != true,
            ShapeCount = _nodes,
        };
    }

    private void Append(SvgGroupNode parent, SvgXmlElement element, SvgStyle inherited, int depth, int references)
    {
        if (depth > MaxDepth || _nodes >= MaxNodes)
            return;

        var declarations = Declarations(element);
        if (Value(element, declarations, "display")?.Trim() == "none")
            return;

        var style = ResolveStyle(element, declarations, inherited);
        var transform = SvgValues.Transform(element.Attribute("transform"));
        var clip = ResolveClip(element, declarations, out var clipEvenOdd);
        var opacity = SvgValues.Opacity(Value(element, declarations, "opacity"));

        switch (element.Name)
        {
            case "g" or "a":
                var group = Group(transform, clip, clipEvenOdd, opacity);
                foreach (var child in element.Children)
                    Append(group, child, style, depth + 1, references);

                Add(parent, group);
                break;

            // Only the first alternative of a <switch> is drawn, as a viewer would do.
            case "switch":
                var chosen = Group(transform, clip, clipEvenOdd, opacity);
                if (element.Children.Count > 0)
                    Append(chosen, element.Children[0], style, depth + 1, references);

                Add(parent, chosen);
                break;

            case "svg":
                var nested = Group(transform, clip, clipEvenOdd, opacity);
                AppendViewport(nested, element, style, null, null, depth, references);
                Add(parent, nested);
                break;

            case "use":
                AppendUse(parent, element, style, transform, clip, clipEvenOdd, opacity, depth, references);
                break;

            case "path" or "rect" or "circle" or "ellipse" or "line" or "polyline" or "polygon":
                if (style.Hidden || (!style.Fill.Paints && !style.Stroke.Paints))
                    return;

                if (ShapePath(element) is not { Count: > 0 } path)
                    return;

                _nodes++;
                parent.Children.Add(new SvgShapeNode
                {
                    Path = path,
                    Style = style,
                    Transform = transform,
                    Clip = clip,
                    ClipEvenOdd = clipEvenOdd,
                    Opacity = opacity,
                });
                break;

            // defs, clipPath, mask, pattern, gradients, text, image, filters and metadata are not drawn here.
            default:
                break;
        }
    }

    /// <summary>A nested viewport: &lt;svg&gt; and a &lt;symbol&gt; reached through &lt;use&gt;.</summary>
    private void AppendViewport(SvgGroupNode parent, SvgXmlElement element, SvgStyle style, float? useWidth, float? useHeight, int depth, int references)
    {
        var x = SvgValues.Length(element.Attribute("x"), _viewportWidth);
        var y = SvgValues.Length(element.Attribute("y"), _viewportHeight);
        var width = useWidth ?? SvgValues.Length(element.Attribute("width"), _viewportWidth, _viewportWidth);
        var height = useHeight ?? SvgValues.Length(element.Attribute("height"), _viewportHeight, _viewportHeight);
        if (width <= 0 || height <= 0)
            return;

        // The viewport clips its contents; its viewBox then scales them into it.
        var clip = new SvgPath();
        clip.AddRectangle(0, 0, width, height, 0, 0);
        var viewport = new SvgGroupNode { Transform = Matrix.Translation(x, y), Clip = clip };

        var viewBox = Numbers(element.Attribute("viewBox"));
        var content = viewBox.Length >= 4 && viewBox[2] > 0 && viewBox[3] > 0
            ? new SvgGroupNode { Transform = ViewBoxMatrix(viewBox[0], viewBox[1], viewBox[2], viewBox[3], width, height, element) }
            : viewport;

        foreach (var child in element.Children)
            Append(content, child, style, depth + 1, references);

        if (content != viewport)
            Add(viewport, content);

        Add(parent, viewport);
    }

    private void AppendUse(
        SvgGroupNode parent,
        SvgXmlElement element,
        SvgStyle style,
        Matrix transform,
        SvgPath? clip,
        bool clipEvenOdd,
        float opacity,
        int depth,
        int references)
    {
        if (references >= MaxReferenceDepth)
            return;

        if (element.Attribute("href") is not { Length: > 1 } href || href[0] != '#')
            return;

        if (!_byId.TryGetValue(href[1..], out var target) || target == element)
            return;

        var x = SvgValues.Length(element.Attribute("x"), _viewportWidth);
        var y = SvgValues.Length(element.Attribute("y"), _viewportHeight);

        // The referenced content is placed at (x, y) inside the transform of the <use> element itself.
        var group = Group(Matrix.Multiply(Matrix.Translation(x, y), transform), clip, clipEvenOdd, opacity);

        if (target.Name is "symbol" or "svg")
        {
            var width = element.Attributes.ContainsKey("width") ? SvgValues.Length(element.Attribute("width"), _viewportWidth) : (float?)null;
            var height = element.Attributes.ContainsKey("height") ? SvgValues.Length(element.Attribute("height"), _viewportHeight) : (float?)null;
            AppendViewport(group, target, style, width, height, depth + 1, references + 1);
        }
        else
        {
            Append(group, target, style, depth + 1, references + 1);
        }

        Add(parent, group);
    }

    private SvgGroupNode Group(Matrix transform, SvgPath? clip, bool clipEvenOdd, float opacity)
    {
        _nodes++;
        return new SvgGroupNode { Transform = transform, Clip = clip, ClipEvenOdd = clipEvenOdd, Opacity = opacity };
    }

    private static void Add(SvgGroupNode parent, SvgGroupNode group)
    {
        // Groups that ended up empty would only cost output bytes.
        if (group.Children.Count > 0)
            parent.Children.Add(group);
    }

    /// <summary>Maps a viewBox onto a viewport, honouring preserveAspectRatio.</summary>
    private static Matrix ViewBoxMatrix(float x, float y, float width, float height, float viewportWidth, float viewportHeight, SvgXmlElement element)
    {
        var alignment = element.Attribute("preserveAspectRatio") ?? "";
        var scaleX = viewportWidth / width;
        var scaleY = viewportHeight / height;

        if (!alignment.Contains("none", StringComparison.OrdinalIgnoreCase))
        {
            var uniform = alignment.Contains("slice", StringComparison.OrdinalIgnoreCase)
                ? MathF.Max(scaleX, scaleY)
                : MathF.Min(scaleX, scaleY);
            scaleX = scaleY = uniform;
        }

        var offsetX = (viewportWidth - width * scaleX) / 2;
        var offsetY = (viewportHeight - height * scaleY) / 2;
        if (alignment.Contains("xMin", StringComparison.Ordinal))
            offsetX = 0;
        else if (alignment.Contains("xMax", StringComparison.Ordinal))
            offsetX = viewportWidth - width * scaleX;

        if (alignment.Contains("YMin", StringComparison.Ordinal))
            offsetY = 0;
        else if (alignment.Contains("YMax", StringComparison.Ordinal))
            offsetY = viewportHeight - height * scaleY;

        return Matrix.Multiply(
            Matrix.Multiply(Matrix.Translation(-x, -y), Matrix.Scaling(scaleX, scaleY)),
            Matrix.Translation(offsetX, offsetY));
    }

    // ---- Shapes ---------------------------------------------------------------------------------

    private SvgPath? ShapePath(SvgXmlElement element)
    {
        var path = new SvgPath();
        var diagonal = Diagonal;

        switch (element.Name)
        {
            case "path":
                if (element.Attribute("d") is not { Length: > 0 } data)
                    return null;

                SvgPathParser.Parse(data, path, MaxPathCommands);
                break;

            case "rect":
                var width = SvgValues.Length(element.Attribute("width"), _viewportWidth);
                var height = SvgValues.Length(element.Attribute("height"), _viewportHeight);
                if (width <= 0 || height <= 0)
                    return null;

                // One of rx and ry may be left out, in which case it equals the other.
                var hasRx = element.Attributes.ContainsKey("rx");
                var hasRy = element.Attributes.ContainsKey("ry");
                var rx = hasRx ? SvgValues.Length(element.Attribute("rx"), _viewportWidth) : 0;
                var ry = hasRy ? SvgValues.Length(element.Attribute("ry"), _viewportHeight) : 0;
                if (!hasRx)
                    rx = ry;

                if (!hasRy)
                    ry = rx;

                path.AddRectangle(
                    SvgValues.Length(element.Attribute("x"), _viewportWidth),
                    SvgValues.Length(element.Attribute("y"), _viewportHeight),
                    width,
                    height,
                    rx,
                    ry);
                break;

            case "circle":
                var radius = SvgValues.Length(element.Attribute("r"), diagonal);
                if (radius <= 0)
                    return null;

                path.AddEllipse(
                    SvgValues.Length(element.Attribute("cx"), _viewportWidth),
                    SvgValues.Length(element.Attribute("cy"), _viewportHeight),
                    radius,
                    radius);
                break;

            case "ellipse":
                var ellipseRx = SvgValues.Length(element.Attribute("rx"), _viewportWidth);
                var ellipseRy = SvgValues.Length(element.Attribute("ry"), _viewportHeight);
                if (ellipseRx <= 0 || ellipseRy <= 0)
                    return null;

                path.AddEllipse(
                    SvgValues.Length(element.Attribute("cx"), _viewportWidth),
                    SvgValues.Length(element.Attribute("cy"), _viewportHeight),
                    ellipseRx,
                    ellipseRy);
                break;

            case "line":
                path.MoveTo(
                    SvgValues.Length(element.Attribute("x1"), _viewportWidth),
                    SvgValues.Length(element.Attribute("y1"), _viewportHeight));
                path.LineTo(
                    SvgValues.Length(element.Attribute("x2"), _viewportWidth),
                    SvgValues.Length(element.Attribute("y2"), _viewportHeight));
                break;

            case "polyline" or "polygon":
                var points = Numbers(element.Attribute("points"));
                if (points.Length < 4)
                    return null;

                path.MoveTo(points[0], points[1]);
                for (var i = 2; i + 1 < points.Length; i += 2)
                    path.LineTo(points[i], points[i + 1]);

                if (element.Name == "polygon")
                    path.ClosePath();

                break;

            default:
                return null;
        }

        return path;
    }

    /// <summary>The length a percentage refers to when it is neither horizontal nor vertical.</summary>
    private float Diagonal => MathF.Sqrt(_viewportWidth * _viewportWidth + _viewportHeight * _viewportHeight) / MathF.Sqrt(2);

    private static float[] Numbers(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];

        var scanner = new SvgScanner(text);
        var numbers = new List<float>(8);
        while (numbers.Count < 8192 && scanner.TryReadNumber(out var number))
            numbers.Add(number);

        return [.. numbers];
    }

    // ---- Styles ---------------------------------------------------------------------------------

    private Dictionary<string, string>? Declarations(SvgXmlElement element)
    {
        var inline = element.Attribute("style");
        if (_styles.IsEmpty && string.IsNullOrWhiteSpace(inline))
            return null;

        // Presentation attributes are the weakest; style sheets override them and the style attribute wins.
        var declarations = new Dictionary<string, string>(StringComparer.Ordinal);
        _styles.Apply(new StyleTarget(element.Name.ToLowerInvariant(), element.Attribute("id"), Classes(element)), declarations);
        if (!string.IsNullOrWhiteSpace(inline))
        {
            foreach (var declaration in StyleSheet.ParseDeclarations(inline))
                declarations[declaration.Key] = declaration.Value;
        }

        return declarations;
    }

    private static string[] Classes(SvgXmlElement element) =>
        element.Attribute("class")?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) ?? [];

    /// <summary>The value of a property, from the style sheet and style attribute first, then the attribute.</summary>
    private static string? Value(SvgXmlElement element, Dictionary<string, string>? declarations, string property)
    {
        var value = declarations?.GetValueOrDefault(property) ?? element.Attribute(property);

        // "inherit" means the parent's value, which the caller already holds.
        return value == null || value.Trim() == "inherit" ? null : value;
    }

    private SvgStyle ResolveStyle(SvgXmlElement element, Dictionary<string, string>? declarations, SvgStyle inherited)
    {
        var style = inherited;

        // The color property comes first: currentColor refers to it.
        if (Value(element, declarations, "color") is { } colorText && SvgValues.TryColor(colorText, out var color))
            style = style with { CurrentColor = color };

        if (Value(element, declarations, "fill") is { } fill)
            style = style with { Fill = ParsePaint(fill, style.CurrentColor) };

        if (Value(element, declarations, "stroke") is { } stroke)
            style = style with { Stroke = ParsePaint(stroke, style.CurrentColor) };

        if (Value(element, declarations, "stroke-width") is { } strokeWidth)
            style = style with { StrokeWidth = MathF.Max(SvgValues.Length(strokeWidth, Diagonal, 1), 0) };

        if (Value(element, declarations, "fill-opacity") is { } fillOpacity)
            style = style with { FillOpacity = SvgValues.Opacity(fillOpacity) };

        if (Value(element, declarations, "stroke-opacity") is { } strokeOpacity)
            style = style with { StrokeOpacity = SvgValues.Opacity(strokeOpacity) };

        if (Value(element, declarations, "fill-rule") is { } fillRule)
            style = style with { EvenOdd = fillRule.Trim() == "evenodd" };

        if (Value(element, declarations, "stroke-linecap") is { } cap)
            style = style with { LineCap = cap.Trim() switch { "round" => 1, "square" => 2, _ => 0 } };

        if (Value(element, declarations, "stroke-linejoin") is { } join)
            style = style with { LineJoin = join.Trim() switch { "round" => 1, "bevel" => 2, _ => 0 } };

        if (Value(element, declarations, "stroke-dasharray") is { } dashes)
            style = style with { Dashes = SvgValues.Dashes(dashes, Diagonal) };

        if (Value(element, declarations, "stroke-dashoffset") is { } dashOffset)
            style = style with { DashOffset = SvgValues.Length(dashOffset, Diagonal) };

        if (Value(element, declarations, "visibility") is { } visibility)
            style = style with { Hidden = visibility.Trim() is "hidden" or "collapse" };

        return style;
    }

    private SvgPaint ParsePaint(string text, Color currentColor)
    {
        text = text.Trim();
        if (text.Length == 0 || text is "none" or "transparent")
            return SvgPaint.None;

        if (string.Equals(text, "currentColor", StringComparison.OrdinalIgnoreCase))
            return SvgPaint.Solid(currentColor);

        if (text.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
        {
            var close = text.IndexOf(')');
            if (close < 0)
                return SvgPaint.None;

            if (Reference(text[..(close + 1)]) is { } id && _byId.TryGetValue(id, out var target) && Gradient(target, 0) is { } gradient)
                return SvgPaint.FromGradient(gradient);

            // A paint server may be followed by a fallback color: fill="url(#missing) blue".
            var fallback = text[(close + 1)..].Trim();
            return fallback.Length > 0 ? ParsePaint(fallback, currentColor) : SvgPaint.None;
        }

        // Unsupported paint servers (patterns) and unknown keywords leave the shape unpainted.
        return SvgValues.TryColor(text, out var color) ? SvgPaint.Solid(color) : SvgPaint.None;
    }

    private static string? Reference(string url)
    {
        var open = url.IndexOf('(');
        var close = url.LastIndexOf(')');
        if (open < 0 || close <= open)
            return null;

        var id = url[(open + 1)..close].Trim().Trim('"', '\'');
        return id.Length > 1 && id[0] == '#' ? id[1..] : null;
    }

    private SvgPath? ResolveClip(SvgXmlElement element, Dictionary<string, string>? declarations, out bool evenOdd)
    {
        evenOdd = false;
        if (Value(element, declarations, "clip-path") is not { Length: > 0 } text)
            return null;

        if (Reference(text) is not { } id || !_byId.TryGetValue(id, out var clipPath) || clipPath.Name != "clipPath")
            return null;

        evenOdd = Value(clipPath, Declarations(clipPath), "clip-rule")?.Trim() == "evenodd";

        // The union of the shapes inside the clipPath, flattened into one path.
        var path = new SvgPath();
        foreach (var child in clipPath.Children)
        {
            if (ShapePath(child) is { Count: > 0 } shape)
                path.AppendTransformed(shape, SvgValues.Transform(child.Attribute("transform")));
        }

        return path.Count > 0 ? path : null;
    }

    // ---- Gradients ------------------------------------------------------------------------------

    private SvgGradient? Gradient(SvgXmlElement element, int depth)
    {
        if (element.Name is not ("linearGradient" or "radialGradient"))
            return null;

        // Attributes and stops may come from another gradient through href.
        var chain = new List<SvgXmlElement> { element };
        var current = element;
        while (chain.Count <= MaxReferenceDepth &&
            current.Attribute("href") is { Length: > 1 } href &&
            href[0] == '#' &&
            _byId.TryGetValue(href[1..], out var referenced) &&
            !chain.Contains(referenced))
        {
            chain.Add(referenced);
            current = referenced;
        }

        string? Attribute(string name)
        {
            foreach (var link in chain)
            {
                if (link.Attribute(name) is { Length: > 0 } value)
                    return value;
            }

            return null;
        }

        var stops = Stops(chain);
        if (stops.Length == 0)
            return null;

        var userSpace = Attribute("gradientUnits")?.Trim() == "userSpaceOnUse";
        var transform = SvgValues.Transform(Attribute("gradientTransform"));

        // In object bounding box units the coordinates are fractions of the box, so percentages are of 1.
        float horizontal = userSpace ? _viewportWidth : 1;
        float vertical = userSpace ? _viewportHeight : 1;
        var diagonal = userSpace ? Diagonal : 1;

        if (element.Name == "linearGradient")
        {
            return new SvgGradient(
                false,
                SvgValues.Length(Attribute("x1"), horizontal),
                SvgValues.Length(Attribute("y1"), vertical),
                0,
                SvgValues.Length(Attribute("x2"), horizontal, horizontal),
                SvgValues.Length(Attribute("y2"), vertical),
                0,
                stops,
                userSpace,
                transform);
        }

        var centerX = SvgValues.Length(Attribute("cx"), horizontal, horizontal / 2);
        var centerY = SvgValues.Length(Attribute("cy"), vertical, vertical / 2);
        var radius = SvgValues.Length(Attribute("r"), diagonal, diagonal / 2);
        if (radius <= 0)
            return null;

        // PDF draws a radial gradient between two circles: the focus point and the outer circle.
        return new SvgGradient(
            true,
            SvgValues.Length(Attribute("fx"), horizontal, centerX),
            SvgValues.Length(Attribute("fy"), vertical, centerY),
            0,
            centerX,
            centerY,
            radius,
            stops,
            userSpace,
            transform);
    }

    private ColorStop[] Stops(List<SvgXmlElement> chain)
    {
        List<SvgXmlElement>? elements = null;
        foreach (var link in chain)
        {
            elements = link.Children.FindAll(child => child.Name == "stop");
            if (elements.Count > 0)
                break;
        }

        if (elements is not { Count: > 0 })
            return [];

        var stops = new List<ColorStop>(elements.Count + 2);
        var previous = 0f;
        foreach (var element in elements)
        {
            if (stops.Count >= MaxStops)
                break;

            var declarations = Declarations(element);
            var offsetText = Value(element, declarations, "offset");
            var offset = SvgValues.Number(offsetText);
            if (offsetText?.Contains('%') == true)
                offset /= 100;

            // Offsets never decrease: a stop before the previous one is pulled up to it.
            offset = MathF.Max(Math.Clamp(offset, 0, 1), previous);
            previous = offset;

            var color = Colors.Black;
            if (Value(element, declarations, "stop-color") is { } text && SvgValues.TryColor(text, out var parsed))
                color = parsed;

            stops.Add(new ColorStop(offset, color, SvgValues.Opacity(Value(element, declarations, "stop-opacity"))));
        }

        return Normalize(stops);
    }

    /// <summary>
    /// A PDF stitching function needs offsets that strictly increase and span the whole domain, while SVG
    /// allows repeated offsets for a hard transition and gradients that do not reach 0 or 1.
    /// </summary>
    private static ColorStop[] Normalize(List<ColorStop> stops)
    {
        const float minimumStep = 1e-4f;

        if (stops.Count == 0)
            return [];

        if (stops.Count == 1)
            return [stops[0]];

        var normalized = new List<ColorStop>(stops.Count + 2);
        if (stops[0].Offset > 0)
            normalized.Add(stops[0] with { Offset = 0 });

        foreach (var stop in stops)
        {
            if (normalized.Count == 0)
            {
                normalized.Add(stop);
                continue;
            }

            var minimum = normalized[^1].Offset + minimumStep;
            if (stop.Offset >= minimum)
            {
                normalized.Add(stop);
            }
            else if (minimum < 1)
            {
                // A hard transition: the next color starts as close to the previous stop as the format allows.
                normalized.Add(stop with { Offset = minimum });
            }
            else
            {
                // No room left; the last color wins.
                normalized[^1] = normalized[^1] with { Color = stop.Color };
            }
        }

        if (normalized[^1].Offset < 1)
            normalized.Add(normalized[^1] with { Offset = 1 });

        return [.. normalized];
    }
}
