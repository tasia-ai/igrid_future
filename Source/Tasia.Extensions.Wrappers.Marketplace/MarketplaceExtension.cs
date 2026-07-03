using System;
using Tasia.Extensions.SDK;

namespace TasiaAddon.Marketplace;

public class MarketplaceExtension : IGridExtension
{
    private IExtensionContext? _context;

    public string Name => "TasiaAddon.Marketplace";
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
