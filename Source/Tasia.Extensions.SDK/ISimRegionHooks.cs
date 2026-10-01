namespace Tasia.Extensions.SDK;

public interface ISimRegionHooks
{
    void OnRegionAdded(object scene);
    void OnRegionLoaded(object scene);
    void OnRegionRemoved(object scene);
    void OnRegionShutdown(object scene);
}

public interface IRobustHooks
{
    void OnRobustStartup();
    void OnRobustShutdown();
}
