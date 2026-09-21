namespace Papira.Infrastructure;

internal readonly record struct Size(float Width, float Height)
{
    public const float Epsilon = 0.001f;
}
