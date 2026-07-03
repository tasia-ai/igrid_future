using System;
using Tasia.Extensions.SDK;

namespace TasiaAddon.MACAudit;

public class MACAuditExtension : IGridExtension
{
    private MacAuditRegionModule? _regionModule;
    private MACAuditLoginService? _loginService;
    private IExtensionContext? _context;

    public string Name => "TasiaAddon.MACAudit";
    public string Version => "1.0.0";

    public void Initialize(IExtensionContext context)
    {
        _context = context;
    }

    public void Start()
    {
        _regionModule = new MacAuditRegionModule();
    }

    public void PostInitialise()
    {
    }

    public void Stop()
    {
        _regionModule?.Close();
        _regionModule = null;
    }

    public void Dispose()
    {
        Stop();
    }
}
