namespace Tasia.Extensions.SDK;

public interface IExtensionLogger
{
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}
