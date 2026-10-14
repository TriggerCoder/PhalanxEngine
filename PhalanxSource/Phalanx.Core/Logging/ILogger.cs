// Port of SpartanEngine source/logging/ILogger.h @ 4c07baa.
namespace Phalanx;

// receives every line once attached (spartan: ILogger), the editor console implements it
public interface ILogger
{
    void Log(string text, LogType type);
}
