namespace Papira;

/// <summary>
/// A place in the layout that accepts exactly one child. Content is added with the extension methods
/// in <see cref="ContainerExtensions"/>; wrappers such as <c>Padding</c> return a new container for their content.
/// </summary>
/// <remarks>Containers are created by Papira; implementing this interface in your own code is not supported.</remarks>
public interface IContainer;
