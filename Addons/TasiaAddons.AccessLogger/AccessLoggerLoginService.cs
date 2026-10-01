using System;
using System.Collections;
using System.IO;
using System.Net;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Services.Interfaces;
using OpenSim.Services.LLLoginService;

namespace TasiaAddons.AccessLogger;

public class AccessLoggerLoginService : ILoginService
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(AccessLoggerLoginService));

    private readonly LLLoginService m_inner;
    private readonly string m_logFilePath;
    private readonly object m_fileLock = new();

    public AccessLoggerLoginService(IConfigSource config)
    {
        IConfig accessConfig = config.Configs["AccessLogger"];
        string logPath = "Data/access.log";
        
        if (accessConfig != null)
        {
            logPath = accessConfig.GetString("LogFile", logPath);
        }

        m_logFilePath = logPath;

        string? directory = Path.GetDirectoryName(Path.GetFullPath(m_logFilePath));
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        m_inner = new LLLoginService(config);

        Log.Info($"[ACCESS LOGGER]: Logging logins to {m_logFilePath}");
    }

    public LoginResponse Login(string firstName, string lastName, string passwd, string startLocation, 
        UUID scopeID, string clientVersion, string channel, string mac, string id0, IPEndPoint clientIP)
    {
        string ipAddress = clientIP?.Address?.ToString() ?? "unknown";
        string gridUri = scopeID != UUID.Zero ? scopeID.ToString() : "local";
        
        string logEntry = $"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss UTC}|LOGIN|{firstName} {lastName}|{ipAddress}|{mac}|{id0}|{gridUri}|{clientVersion}|{channel}";

        WriteToLog(logEntry);

        LoginResponse response = m_inner.Login(firstName, lastName, passwd, startLocation, 
            scopeID, clientVersion, channel, mac, id0, clientIP);

        WriteToLog($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss UTC}|LOGIN_RESULT|{firstName} {lastName}|{(response is FailedLoginResponse ? "FAILED" : "SUCCESS")}");

        return response;
    }

    public Hashtable SetLevel(string firstName, string lastName, string passwd, int level, IPEndPoint clientIP)
    {
        return m_inner.SetLevel(firstName, lastName, passwd, level, clientIP);
    }

    private void WriteToLog(string entry)
    {
        try
        {
            lock (m_fileLock)
            {
                File.AppendAllText(m_logFilePath, entry + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[ACCESS LOGGER]: Failed to write to log: {ex.Message}");
        }
    }
}
