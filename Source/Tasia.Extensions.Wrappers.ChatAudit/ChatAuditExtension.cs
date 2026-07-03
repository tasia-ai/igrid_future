using System;
using System.IO;
using Tasia.Extensions.SDK;

namespace TasiaAddon.ChatAudit;

public class ChatAuditExtension : IGridExtension, ISimRegionHooks
{
    private ChatAuditModule? _module;
    private IExtensionContext? _context;
    private bool _initialized;

    public string Name => "TasiaAddon.ChatAudit";
    public string Version => "1.0.0";

    public void Initialize(IExtensionContext context)
    {
        _context = context;
    }

    public void Start()
    {
        if (_context == null)
            throw new InvalidOperationException("Context not set");
        
        if (_module != null)
            return;

        _module = new ChatAuditModule();
        
        var ctx = new LegacyContextAdapter(_context);
        _module.Configure(ctx);
    }

    public void PostInitialise()
    {
        if (_module == null)
            return;
    }

    public void Stop()
    {
        if (_module != null)
        {
            _module.Close();
            _module = null;
        }
    }

    public void Dispose()
    {
        Stop();
    }

    public void OnRegionAdded(object scene)
    {
        if (_module != null && scene is OpenSim.Region.Framework.Scenes.Scene s)
        {
            _module.AddRegion(s);
        }
    }

    public void OnRegionLoaded(object scene)
    {
        if (_module != null && scene is OpenSim.Region.Framework.Scenes.Scene s)
        {
            _module.RegionLoaded(s);
        }
    }

    public void OnRegionRemoved(object scene)
    {
        if (_module != null && scene is OpenSim.Region.Framework.Scenes.Scene s)
        {
            _module.RemoveRegion(s);
        }
    }

    public void OnRegionShutdown(object scene)
    {
    }

    private class LegacyContextAdapter : TasiaAddons.Abstractions.ITasiaAddonsContext
    {
        private readonly IExtensionContext _ctx;

        public LegacyContextAdapter(IExtensionContext ctx)
        {
            _ctx = ctx;
        }

        public TasiaAddons.Abstractions.ITasiaAddonsAuditService AuditService => TasiaAddons.Abstractions.NullAuditService.Instance;
        public Nini.Config.IConfigSource ConfigSource => throw new NotImplementedException("Config access not available in new architecture - TODO: implement via Compat layer");
    }
}
