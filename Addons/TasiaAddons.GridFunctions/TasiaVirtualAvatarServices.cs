using Nini.Config;
using System.Globalization;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;

namespace TasiaAddons.GridFunctions;

/// <summary>
/// Adds virtual-avatar rows to name/UUID lookups while preserving the configured
/// stock account service for every ordinary grid account.
/// </summary>
public sealed class TasiaVirtualAvatarAccountService : IUserAccountService
{
    private readonly IUserAccountService inner;
    private readonly VirtualAvatarStore store;

    public TasiaVirtualAvatarAccountService(IConfigSource config)
    {
        IConfig options = config.Configs["TasiaGridFunctions"];
        string service = options?.GetString("BaseUserAccountService",
            "OpenSim.Services.UserAccountService.dll:UserAccountService")
            ?? "OpenSim.Services.UserAccountService.dll:UserAccountService";
        inner = ServerUtils.LoadRequiredPlugin<IUserAccountService>(service, [config]);
        store = new VirtualAvatarStore(config);
    }

    public UserAccount GetUserAccount(UUID scopeID, UUID userID)
        => ToAccount(store.Find(userID), scopeID) ?? inner.GetUserAccount(scopeID, userID);

    public UserAccount GetUserAccount(UUID scopeID, string FirstName, string LastName)
        => ToAccount(store.Find(scopeID, FirstName, LastName), scopeID)
            ?? inner.GetUserAccount(scopeID, FirstName, LastName);

    public UserAccount GetUserAccount(UUID scopeID, string Email)
        => inner.GetUserAccount(scopeID, Email);

    public List<UserAccount> GetUserAccounts(UUID scopeID, string query)
    {
        List<UserAccount> accounts = inner.GetUserAccounts(scopeID, query) ?? [];
        HashSet<UUID> seen = accounts.Select(account => account.PrincipalID).ToHashSet();
        foreach (VirtualAvatarProfile profile in store.Search(query))
        {
            UserAccount account = ToAccount(profile, scopeID);
            if (account != null && seen.Add(account.PrincipalID))
                accounts.Add(account);
        }
        return accounts;
    }

    public List<UserAccount> GetUserAccountsWhere(UUID scopeID, string where)
        => inner.GetUserAccountsWhere(scopeID, where);

    public List<UserAccount> GetUserAccounts(UUID scopeID, List<string> IDs)
    {
        List<UserAccount> accounts = inner.GetUserAccounts(scopeID, IDs) ?? [];
        HashSet<UUID> seen = accounts.Select(account => account.PrincipalID).ToHashSet();
        foreach (VirtualAvatarProfile profile in store.FindMany(IDs))
        {
            UserAccount account = ToAccount(profile, scopeID);
            if (account != null && seen.Add(account.PrincipalID))
                accounts.Add(account);
        }
        return accounts;
    }

    public bool StoreUserAccount(UserAccount data)
    {
        if (data != null && store.Find(data.PrincipalID) != null)
            return false;
        return inner.StoreUserAccount(data);
    }

    public bool SetDisplayName(UUID agentID, string displayName)
        => store.Find(agentID) != null
            ? store.UpdateDisplayName(agentID, displayName)
            : inner.SetDisplayName(agentID, displayName);

    public void InvalidateCache(UUID userID) => inner.InvalidateCache(userID);

    private static UserAccount ToAccount(VirtualAvatarProfile profile, UUID scopeID)
    {
        if (profile == null)
            return null;
        int created = profile.Created;
        if (DateTime.TryParseExact(profile.BornOn, ["M/d/yyyy", "MM/dd/yyyy", "yyyy-MM-dd"],
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTime bornOn))
            created = (int)new DateTimeOffset(DateTime.SpecifyKind(bornOn, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return new UserAccount(scopeID, profile.VirtualID, profile.FirstName, profile.LastName, string.Empty)
        {
            DisplayName = profile.DisplayName,
            UserTitle = profile.UserTitle,
            UserLevel = 0,
            UserFlags = 0,
            Created = created,
            LocalToGrid = true,
            ServiceURLs = new Dictionary<string, object>()
        };
    }
}

/// <summary>Serves virtual-avatar profile properties and delegates normal profiles to OpenSim.</summary>
public sealed class TasiaVirtualAvatarProfilesService : IUserProfilesService
{
    private readonly IUserProfilesService inner;
    private readonly VirtualAvatarStore store;

    public TasiaVirtualAvatarProfilesService(IConfigSource config, string configName)
    {
        IConfig options = config.Configs["TasiaGridFunctions"];
        string service = options?.GetString("BaseUserProfilesService",
            "OpenSim.Services.UserProfilesService.dll:UserProfilesService")
            ?? "OpenSim.Services.UserProfilesService.dll:UserProfilesService";
        inner = ServerUtils.LoadRequiredPlugin<IUserProfilesService>(service, [config, configName]);
        store = new VirtualAvatarStore(config);
    }

    public OSD AvatarClassifiedsRequest(UUID creatorId) => inner.AvatarClassifiedsRequest(creatorId);
    public bool ClassifiedUpdate(UserClassifiedAdd ad, ref string result) => inner.ClassifiedUpdate(ad, ref result);
    public bool ClassifiedInfoRequest(ref UserClassifiedAdd ad, ref string result) => inner.ClassifiedInfoRequest(ref ad, ref result);
    public bool ClassifiedDelete(UUID recordId) => inner.ClassifiedDelete(recordId);
    public OSD AvatarPicksRequest(UUID creatorId) => inner.AvatarPicksRequest(creatorId);
    public bool PickInfoRequest(ref UserProfilePick pick, ref string result) => inner.PickInfoRequest(ref pick, ref result);
    public bool PicksUpdate(ref UserProfilePick pick, ref string result) => inner.PicksUpdate(ref pick, ref result);
    public bool PicksDelete(UUID pickId) => inner.PicksDelete(pickId);
    public bool AvatarNotesRequest(ref UserProfileNotes note) => inner.AvatarNotesRequest(ref note);
    public bool NotesUpdate(ref UserProfileNotes note, ref string result) => inner.NotesUpdate(ref note, ref result);

    public bool AvatarPropertiesRequest(ref UserProfileProperties prop, ref string result)
    {
        VirtualAvatarProfile profile = store.Find(prop.UserId);
        if (profile == null)
            return inner.AvatarPropertiesRequest(ref prop, ref result);

        prop = new UserProfileProperties
        {
            UserId = profile.VirtualID,
            AboutText = profile.AboutText,
            ImageId = profile.ProfileImage,
            FirstLifeImageId = profile.FirstLifeImage,
            FirstLifeText = profile.FirstLifeText,
            WebUrl = profile.WebUrl,
            PublishProfile = true,
            PublishMature = true
        };
        result = "success";
        return true;
    }

    public bool AvatarPropertiesUpdate(ref UserProfileProperties prop, ref string result)
    {
        if (store.Find(prop.UserId) != null)
        {
            result = "Virtual-avatar profiles are managed by maChangeGridProfile.";
            return false;
        }
        return inner.AvatarPropertiesUpdate(ref prop, ref result);
    }

    public bool UserPreferencesRequest(ref UserPreferences pref, ref string result) => inner.UserPreferencesRequest(ref pref, ref result);
    public bool UserPreferencesUpdate(ref UserPreferences pref, ref string result) => inner.UserPreferencesUpdate(ref pref, ref result);
    public bool AvatarInterestsUpdate(UserProfileProperties prop, ref string result)
    {
        if (store.Find(prop.UserId) != null)
        {
            result = "Virtual-avatar profiles are managed by maChangeGridProfile.";
            return false;
        }
        return inner.AvatarInterestsUpdate(prop, ref result);
    }
    public OSD AvatarImageAssetsRequest(UUID avatarId) => inner.AvatarImageAssetsRequest(avatarId);
    public bool RequestUserAppData(ref UserAppData prop, ref string result) => inner.RequestUserAppData(ref prop, ref result);
    public bool SetUserAppData(UserAppData prop, ref string result) => inner.SetUserAppData(prop, ref result);
}
