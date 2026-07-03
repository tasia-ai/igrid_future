using System;
using Tasia.Extensions.SDK;

namespace TasiaAddon.RemoteSound;

public class RemoteSoundExtension : IGridExtension, ISimRegionHooks
{
    private IExtensionContext? _context;

    public string Name => "TasiaAddon.RemoteSound";
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

    public void OnRegionAdded(object scene)
    {
    }

    public void OnRegionLoaded(object scene)
    {
    }

    public void OnRegionRemoved(object scene)
    {
    }

    public void OnRegionShutdown(object scene)
    {
    }
}
