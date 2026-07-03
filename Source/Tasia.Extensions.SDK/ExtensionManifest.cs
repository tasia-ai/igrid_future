namespace Tasia.Extensions.SDK;

public enum ExtensionTarget
{
    Sim,
    Robust,
    Both
}

public class ExtensionManifest
{
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Description { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public ExtensionTarget Target { get; set; } = ExtensionTarget.Both;
    public string EntryPoint { get; set; } = string.Empty;
    public string MinApiVersion { get; set; } = "1.0.0";
    public bool Enabled { get; set; } = true;
    public List<string> Dependencies { get; set; } = new();
}
