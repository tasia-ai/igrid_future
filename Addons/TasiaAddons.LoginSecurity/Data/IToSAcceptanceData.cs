namespace TasiaAddons.LoginSecurity.Data
{
    public interface IToSAcceptanceData
    {
        bool HasAccepted(string userId, int tosVersion);
        bool RecordAcceptance(string userId, int tosVersion);
        bool ResetAll(int newVersion);
        int GetUserVersion(string userId);
    }
}
