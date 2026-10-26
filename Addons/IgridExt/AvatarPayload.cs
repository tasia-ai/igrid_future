using System.Collections.Generic;

namespace IgridExt;

public class AvatarPayload
{
	public string Agent { get; set; }

	public string VisualParams { get; set; }

	public double[] AvatarSize { get; set; }

	public double FeetOffset { get; set; }

	public double Height { get; set; }

	public Dictionary<string, string> Textures { get; set; }

	public Dictionary<string, int> TextureBakes { get; set; }

	public List<int> BakeIndices { get; set; }

	public List<WearablePayload> Wearables { get; set; }
}
