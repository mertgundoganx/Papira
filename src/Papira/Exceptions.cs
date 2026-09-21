namespace Papira;

/// <summary>Thrown when a document is composed incorrectly (e.g. two children in one container).</summary>
public sealed class DocumentComposeException(string message) : Exception(message);

/// <summary>Thrown when content cannot be laid out, e.g. an element larger than the page.</summary>
public sealed class DocumentLayoutException(string message) : Exception(message);
