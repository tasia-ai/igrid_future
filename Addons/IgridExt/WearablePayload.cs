using System.Collections.Generic;

namespace IgridExt;

public class WearablePayload
{
	public int Type { get; set; }

	public string Item { get; set; }

	public string Asset { get; set; }

	public string Mesh { get; set; }

	public List<TextureSlot> Textures { get; set; }
}
