using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;
using log4net;

namespace IgridExt;

public class IgridExtServiceInConnector : ServiceConnector
{
	private const string ConfigSection = "IgridExtService";

	private const int MaxAssetBytes = 33554432;

	private static readonly ILog Log = LogManager.GetLogger("IgridExt");

	private bool _enabled;

	private string _apiKey = string.Empty;

	private IAvatarService _avatarService;

	private IAssetService _assetService;

	private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
	{
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
	};

	public IgridExtServiceInConnector(IConfigSource configSource, IHttpServer server, string configName)
		: base(configSource, server, configName)
	{
		try
		{
			IConfig config = configSource?.Configs?["IgridExtService"];
			if (config == null)
			{
				Log.Warn("[IgridExt] no [IgridExtService] section, channel disabled");
				return;
			}
			if (!config.GetBoolean("Enabled", defaultValue: true))
			{
				Log.Info("[IgridExt] disabled by configuration");
				return;
			}
			string text = config.GetString("ApiKeyFile", string.Empty);
			if (string.IsNullOrWhiteSpace(text) || !File.Exists(text))
			{
				Log.Warn("[IgridExt] ApiKeyFile '" + text + "' not found, channel disabled");
				return;
			}
			_apiKey = File.ReadAllText(text, Encoding.UTF8).Trim();
			if (_apiKey.Length == 0)
			{
				Log.Warn("[IgridExt] API key file is empty, channel disabled");
				return;
			}
			_avatarService = ServerUtils.LoadPlugin<IAvatarService>(config.GetString("AvatarServiceModule", ""), new object[1] { configSource });
			_assetService = ServerUtils.LoadPlugin<IAssetService>(config.GetString("AssetServiceModule", ""), new object[1] { configSource });
			if (_avatarService == null && _assetService == null)
			{
				Log.Warn("[IgridExt] neither AvatarServiceModule nor AssetServiceModule resolved, channel disabled");
				return;
			}
			Register(server);
			_enabled = true;
			Log.Info($"[IgridExt] viewer channel ready (port {server?.Port}), key {_apiKey.Length} chars");
		}
		catch (Exception ex)
		{
			_enabled = false;
			Log.Error("[IgridExt] failed to start, channel disabled: " + ex);
		}
	}

	private void Register(IHttpServer server)
	{
		if (server != null)
		{
			server.AddSimpleStreamHandler(new SimpleStreamHandler("/igridext/avatar", delegate(IOSHttpRequest request, IOSHttpResponse response)
			{
				HandleAvatar(request, response);
			}));
			server.AddSimpleStreamHandler(new SimpleStreamHandler("/igridext/asset", delegate(IOSHttpRequest request, IOSHttpResponse response)
			{
				HandleAsset(request, response);
			}));
		}
	}

	private bool Authorised(string supplied)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(_apiKey ?? string.Empty);
		byte[] bytes2 = Encoding.UTF8.GetBytes(supplied ?? string.Empty);
		if (bytes.Length != bytes2.Length)
		{
			return false;
		}
		return CryptographicOperations.FixedTimeEquals(bytes, bytes2);
	}

	private static void WriteJson(IOSHttpResponse response, int code, string json)
	{
		byte[] bytes = Encoding.UTF8.GetBytes(json ?? "{}");
		response.ContentType = "application/json";
		response.RawBuffer = bytes;
		response.RawBufferLen = bytes.Length;
		response.StatusCode = code;
	}

	private static void Fail(IOSHttpResponse response, int code, string reason)
	{
		WriteJson(response, code, "{\"error\":\"" + reason + "\"}");
	}

	private void HandleAvatar(IOSHttpRequest request, IOSHttpResponse response)
	{
		try
		{
			if (!_enabled)
			{
				Fail(response, 503, "channel_disabled");
				return;
			}
			if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
			{
				Fail(response, 405, "only GET is accepted");
				return;
			}
			if (!Authorised(request.QueryString["key"]))
			{
				Fail(response, 401, "unauthorized");
				return;
			}
			if (!UUID.TryParse(request.QueryString["agent"], out var result) || result == UUID.Zero)
			{
				Fail(response, 400, "agent must be a UUID");
				return;
			}
			if (_avatarService == null)
			{
				Fail(response, 503, "avatar_service_unavailable");
				return;
			}
			AvatarPayload value = BuildPayload(result);
			WriteJson(response, 200, JsonSerializer.Serialize(value, JsonOpts));
		}
		catch (Exception ex)
		{
			Log.Warn("[IgridExt] avatar handler failed: " + ex.Message);
			Fail(response, 503, "handler_error");
		}
	}

	private void HandleAsset(IOSHttpRequest request, IOSHttpResponse response)
	{
		try
		{
			if (!_enabled)
			{
				Fail(response, 503, "channel_disabled");
				return;
			}
			if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
			{
				Fail(response, 405, "only GET is accepted");
				return;
			}
			if (!Authorised(request.QueryString["key"]))
			{
				Fail(response, 401, "unauthorized");
				return;
			}
			if (!UUID.TryParse(request.QueryString["id"], out var result) || result == UUID.Zero)
			{
				Fail(response, 400, "id must be a UUID");
				return;
			}
			if (_assetService == null)
			{
				Fail(response, 503, "asset_service_unavailable");
				return;
			}
			AssetBase assetBase = _assetService.Get(result.ToString());
			byte[] array = assetBase?.Data;
			if (array == null || array.Length == 0)
			{
				Fail(response, 404, "asset_not_found");
				return;
			}
			if (array.Length > 33554432)
			{
				Fail(response, 413, "asset_too_large");
				return;
			}
			response.ContentType = ContentTypeFor(assetBase.Type);
			response.RawBuffer = array;
			response.RawBufferLen = array.Length;
			response.StatusCode = 200;
		}
		catch (Exception ex)
		{
			Log.Warn("[IgridExt] asset handler failed: " + ex.Message);
			Fail(response, 503, "handler_error");
		}
	}

	private static string ContentTypeFor(int assetType)
	{
		return assetType switch
		{
			0 => "image/jpeg", 
			1 => "audio/mpeg", 
			_ => "application/octet-stream", 
		};
	}

	private AvatarPayload BuildPayload(UUID agent)
	{
		AvatarAppearance appearance = _avatarService.GetAppearance(agent);
		if (appearance == null)
		{
			return null;
		}
		AvatarPayload avatarPayload = new AvatarPayload
		{
			Agent = agent.ToString(),
			VisualParams = ((appearance.VisualParams == null) ? null : Convert.ToBase64String(appearance.VisualParams)),
			FeetOffset = 0.0,
			Height = 1.9,
			Textures = new Dictionary<string, string>(),
			TextureBakes = new Dictionary<string, int>(),
			BakeIndices = new List<int>(),
			Wearables = new List<WearablePayload>()
		};
		Primitive.TextureEntry texture = appearance.Texture;
		if (texture != null)
		{
			byte[] bAKE_INDICES = AvatarAppearance.BAKE_INDICES;
			for (uint num = 0u; num < 45; num++)
			{
				Primitive.TextureEntryFace textureEntryFace = null;
				try
				{
					textureEntryFace = texture.GetFace(num);
				}
				catch
				{
				}
				string text = textureEntryFace?.TextureID.ToString();
				bool num2 = string.IsNullOrEmpty(text) || text == "00000000-0000-0000-0000-000000000000";
				if (bAKE_INDICES != null && num < bAKE_INDICES.Length)
				{
					Dictionary<string, int> textureBakes = avatarPayload.TextureBakes;
					int num3 = bAKE_INDICES[num];
					textureBakes[num3.ToString()] = (int)num;
				}
				if (!num2)
				{
					avatarPayload.Textures[num.ToString()] = text;
				}
			}
		}
		if (AvatarAppearance.BAKE_INDICES != null)
		{
			avatarPayload.BakeIndices.AddRange(((IEnumerable<byte>)AvatarAppearance.BAKE_INDICES).Select((Func<byte, int>)((byte b) => b)));
		}
		try
		{
			AvatarData avatar = _avatarService.GetAvatar(agent);
			if (avatar?.Data != null)
			{
				foreach (KeyValuePair<string, string> datum in avatar.Data)
				{
					if (!datum.Key.StartsWith("Wearable ", StringComparison.Ordinal))
					{
						continue;
					}
					string[] array = datum.Key.Substring(9).Split(':');
					if (array.Length != 0 && int.TryParse(array[0], out var result))
					{
						string[] array2 = (datum.Value ?? string.Empty).Split(':');
						if (array2.Length >= 2 && UUID.TryParse(array2[0], out var result2) && !(result2 == UUID.Zero) && UUID.TryParse(array2[1], out var result3) && !(result3 == UUID.Zero))
						{
							avatarPayload.Wearables.Add(new WearablePayload
							{
								Type = result,
								Item = result2.ToString(),
								Asset = result3.ToString()
							});
						}
					}
				}
				avatarPayload.Wearables.Sort((WearablePayload a, WearablePayload b) => a.Type.CompareTo(b.Type));
			}
		}
		catch (Exception ex)
		{
			Log.Warn("[IgridExt] could not read wearables: " + ex.Message);
		}
		return avatarPayload;
	}
}
