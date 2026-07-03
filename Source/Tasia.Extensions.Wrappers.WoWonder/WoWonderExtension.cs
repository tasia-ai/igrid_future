using System;
using Tasia.Extensions.SDK;

namespace TasiaAddon.WoWonder;

public class WoWonderExtension : IGridExtension
{
    private IExtensionContext? _context;

    public string Name => "TasiaAddon.WoWonder";
    public string Version => "1.0.0";

    public void Initialize(IExtensionContext context)
    {
        _context = context;
    }

    public void Start()
    {
    }

    public void PostInitialise()
    {
    }

    public void Stop()
    {
    }

    public void Dispose()
    {
    }
}
