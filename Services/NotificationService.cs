using System;

namespace par.Services;

public class NotificationService
{
    public event Action<string, string>? OnNotify;

    public void NotifySuccess(string message) => Notify("success", message);
    public void NotifyError(string message) => Notify("error", message);

    private void Notify(string level, string message)
    {
        OnNotify?.Invoke(level, message);
        Console.WriteLine($"{level.ToUpperInvariant()}: {message}");
    }
}
