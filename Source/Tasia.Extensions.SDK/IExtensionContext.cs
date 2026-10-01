namespace Tasia.Extensions.SDK;

public interface IExtensionContext
{
    string BasePath { get; }
    IExtensionLogger Logger { get; }
    object? GetService(Type serviceType);
    T? GetService<T>() where T : class;
}
