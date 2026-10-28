namespace WSA.Core.Models;

public sealed record LogEntry(DateTime Time, string Level, string Category, string Message)
{
    public string Display => $"{Time:HH:mm:ss.fff}  {Level,-5}  {Category,-8}  {Message}";
}
