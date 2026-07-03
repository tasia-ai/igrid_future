namespace Tasia.Extensions.SDK;

public interface IGridExtension
{
    string Name { get; }
    string Version { get; }
    
    void Initialize(IExtensionContext context);
    void Start();
    void Stop();
    void PostInitialise();
    void Dispose();
}
