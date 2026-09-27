namespace Papira;

/// <summary>Error correction level of a QR code: higher levels survive more damage but hold less data.</summary>
public enum QrErrorCorrection
{
    /// <summary>Recovers about 7% of the code.</summary>
    Low,

    /// <summary>Recovers about 15% of the code. A good default.</summary>
    Medium,

    /// <summary>Recovers about 25% of the code.</summary>
    Quartile,

    /// <summary>Recovers about 30% of the code.</summary>
    High,
}
