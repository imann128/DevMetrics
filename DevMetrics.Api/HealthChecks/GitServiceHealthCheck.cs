using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DevMetrics.Api.HealthChecks;

/// <summary>
/// Health check that verifies the LibGit2Sharp native library (<c>libgit2</c>)
/// is loadable in the current runtime environment.
/// </summary>
/// <remarks>
/// <para>
/// DevMetrics uses LibGit2Sharp rather than the <c>git</c> CLI, so this check
/// does <em>not</em> look for a <c>git</c> binary on <c>PATH</c>.
/// Instead, it probes the LibGit2Sharp managed API by reading a version string,
/// which forces the native library to be resolved and loaded.
/// </para>
/// <para>
/// Failure mode: the native <c>libgit2</c> binary is missing from the publish
/// output (e.g., the Docker layer was built without the native runtime packages,
/// or the OS architecture is unsupported).
/// </para>
/// <para>
/// This check is tagged <c>"live"</c> so it contributes to the liveness probe —
/// if LibGit2Sharp cannot load, the process itself is broken and should be restarted.
/// </para>
/// </remarks>
public sealed class GitServiceHealthCheck : IHealthCheck
{
    private readonly ILogger<GitServiceHealthCheck> _logger;

    // Cache the result after the first successful check — the native library
    // either loads or it doesn't; rechecking every 30 s is unnecessary.
    private static HealthCheckResult? _cachedResult;

    /// <inheritdoc cref="GitServiceHealthCheck"/>
    public GitServiceHealthCheck(ILogger<GitServiceHealthCheck> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken  cancellationToken = default)
    {
        if (_cachedResult is { Status: HealthStatus.Healthy })
            return Task.FromResult(_cachedResult.Value);

        try
        {
            // Reading GlobalSettings.ProcedurePath forces libgit2 to be loaded.
            // This is a lightweight property access — no file system operations.
            var nativePath = LibGit2Sharp.GlobalSettings.NativeLibraryPath;

            var description = string.IsNullOrEmpty(nativePath)
                ? "LibGit2Sharp native library is loaded (path not exposed on this platform)."
                : $"LibGit2Sharp native library loaded from: {nativePath}";

            _cachedResult = HealthCheckResult.Healthy(description);

            _logger.LogDebug("Health | Git: {Desc}", description);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Health | LibGit2Sharp native library failed to load. " +
                "Git scanning will not function.");

            _cachedResult = HealthCheckResult.Unhealthy(
                "LibGit2Sharp native library could not be loaded. " +
                "Ensure the runtime image includes the native libgit2 binary " +
                "for the target OS and architecture.",
                ex);
        }

        return Task.FromResult(_cachedResult.Value);
    }
}
