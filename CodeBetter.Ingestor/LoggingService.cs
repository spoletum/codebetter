using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace CodeBetter.Ingestor;

public interface ILoggingService
{
    void LogInformation(string message);
    void LogWarning(string message);
    void LogError(string message, Exception? exception = null);
    Task LogToFileAsync(string message, LogLevel level);
}

public enum LogLevel
{
    Information,
    Warning,
    Error
}

public class LoggingService : ILoggingService
{
    private readonly string _logFilePath;
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public LoggingService(string logFilePath)
    {
        _logFilePath = logFilePath;
        EnsureLogDirectoryExists();
    }

    private void EnsureLogDirectoryExists()
    {
        var directory = Path.GetDirectoryName(_logFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }
    }

    public void LogInformation(string message)
    {
        Console.WriteLine($"[INFO] {message}");
        LogToFileAsync(message, LogLevel.Information).GetAwaiter().GetResult();
    }

    public void LogWarning(string message)
    {
        Console.WriteLine($"[WARN] {message}");
        LogToFileAsync(message, LogLevel.Warning).GetAwaiter().GetResult();
    }

    public void LogError(string message, Exception? exception = null)
    {
        var errorMessage = exception == null 
            ? $"[ERROR] {message}" 
            : $"[ERROR] {message}\nException: {exception}\nStackTrace: {exception.StackTrace}";
        
        Console.Error.WriteLine(errorMessage);
        LogToFileAsync(errorMessage, LogLevel.Error).GetAwaiter().GetResult();
    }

    public async Task LogToFileAsync(string message, LogLevel level)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var logMessage = $"[{timestamp}] [{level}] {message}";
        
        try
        {
            await _semaphore.WaitAsync();
            try
            {
                await File.AppendAllTextAsync(_logFilePath, logMessage + Environment.NewLine);
            }
            finally
            {
                _semaphore.Release();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to write to log file: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _semaphore.Dispose();
    }
} 