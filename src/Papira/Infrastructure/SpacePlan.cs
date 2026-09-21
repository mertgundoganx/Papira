namespace Papira.Infrastructure;

internal enum SpacePlanKind : byte
{
    /// <summary>Element has nothing (left) to draw and occupies no space.</summary>
    Empty,

    /// <summary>Element cannot draw anything in the given space; it must move to the next page.</summary>
    Wrap,

    /// <summary>Element draws part of its content; the rest continues on the next page.</summary>
    Partial,

    /// <summary>Element draws all of its remaining content.</summary>
    Full,
}

internal readonly record struct SpacePlan(SpacePlanKind Kind, float Width, float Height)
{
    public static readonly SpacePlan Empty = new(SpacePlanKind.Empty, 0, 0);
    public static readonly SpacePlan Wrap = new(SpacePlanKind.Wrap, 0, 0);

    public static SpacePlan Full(float width, float height) => new(SpacePlanKind.Full, width, height);
    public static SpacePlan Partial(float width, float height) => new(SpacePlanKind.Partial, width, height);

    public bool IsEmpty => Kind == SpacePlanKind.Empty;
    public bool IsWrap => Kind == SpacePlanKind.Wrap;
    public bool HasContent => Kind is SpacePlanKind.Partial or SpacePlanKind.Full;
}
