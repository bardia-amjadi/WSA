using WSA.Core.Models;

namespace WSA.Core.Interfaces;

/// <summary>Runs the exact java.exe (with the exact environment) that WSA hands to sdkmanager/avdmanager and reports what it is.</summary>
public interface IJavaDiagnostics
{
    Task<JavaReport> ProbeAsync(CancellationToken cancellationToken = default);
}
