using DevMetrics.Application.Commands;
using DevMetrics.Application.DTOs;
using DevMetrics.Application.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace DevMetrics.Api.Pages;

/// <summary>
/// Page model for the DevMetrics dashboard (<c>/</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Data strategy:</b> Injects <see cref="IMediator"/> directly rather than
/// making an HTTP self-call via <c>IHttpClientFactory</c>. Since this page model
/// lives in the same <c>DevMetrics.Api</c> process as the controllers, going
/// through HTTP would add unnecessary latency, a connection, and a
/// serialise/deserialise round-trip. MediatR gives the same isolation without
/// the overhead.
/// </para>
/// <para>
/// <b>If you deploy a standalone SPA instead of Razor Pages</b> and want to call
/// the API from another host, configure <c>IHttpClientFactory</c> in
/// <c>Program.cs</c> as follows:
/// <code>
/// builder.Services.AddHttpClient("DevMetricsAPI", client =>
///     client.BaseAddress = new Uri("http://localhost:5000/"));
/// </code>
/// Then inject <c>IHttpClientFactory</c> here and use:
/// <code>
/// var http = factory.CreateClient("DevMetricsAPI");
/// var repos = await http.GetFromJsonAsync&lt;List&lt;RepositoryDto&gt;&gt;("api/repositories");
/// </code>
/// </para>
/// </remarks>
public class IndexModel : PageModel
{
    private readonly IMediator _mediator;
    private readonly ILogger<IndexModel> _logger;

    // ── Bound properties ───────────────────────────────────────────────────────

    /// <summary>All tracked repositories — drives the table and stat card.</summary>
    public List<RepositoryDto> Repositories { get; private set; } = new();

    /// <summary>14-day dashboard payload — drives the Chart.js chart.</summary>
    public DashboardDataDto? DashboardData { get; private set; }

    /// <summary>
    /// Path entered in the Add Repository form.
    /// Preserved across validation failures so the user doesn't retype it.
    /// </summary>
    [BindProperty]
    public string NewRepositoryPath { get; set; } = string.Empty;

    /// <summary>Error message shown below the Add Repository form on failure.</summary>
    public string? AddRepoError { get; private set; }

    /// <summary>Success message shown below the Add Repository form on success.</summary>
    public string? AddRepoSuccess { get; private set; }

    // ── Constructor ────────────────────────────────────────────────────────────

    /// <inheritdoc cref="IndexModel"/>
    public IndexModel(IMediator mediator, ILogger<IndexModel> logger)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _logger   = logger   ?? throw new ArgumentNullException(nameof(logger));
    }

    // ── Handlers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads repositories and 14-day dashboard data for the initial page render.
    /// Both queries run concurrently via <c>Task.WhenAll</c>.
    /// </summary>
    public async Task OnGetAsync(CancellationToken ct)
    {
        _logger.LogDebug("Index | Loading initial dashboard data");

        try
        {
            // Run both queries concurrently — they touch different DB tables.
            var reposTask     = _mediator.Send(new GetAllRepositoriesQuery(), ct);
            var dashboardTask = _mediator.Send(new GetDashboardDataQuery(Days: 14), ct);

            await Task.WhenAll(reposTask, dashboardTask);

            Repositories  = (await reposTask).ToList();
            DashboardData = await dashboardTask;

            _logger.LogDebug(
                "Index | Loaded {Repos} repos and {Days} day summaries",
                Repositories.Count, DashboardData.DailySummaries.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Index | Failed to load dashboard data");

            // Surface a user-visible error rather than a blank page.
            AddRepoError =
                "Could not load dashboard data. Check the application logs for details.";
        }
    }

    /// <summary>
    /// Handles the Add Repository form submission.
    /// Validates the path, dispatches <see cref="AddRepositoryCommand"/>,
    /// and redirects on success (PRG pattern prevents double-submit).
    /// </summary>
    public async Task<IActionResult> OnPostAddRepositoryAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(NewRepositoryPath))
        {
            ModelState.AddModelError(nameof(NewRepositoryPath),
                "Please enter an absolute path to the repository.");
        }

        if (!ModelState.IsValid)
        {
            // Reload page data so the rest of the page renders correctly.
            await OnGetAsync(ct);
            return Page();
        }

        _logger.LogInformation(
            "Index | AddRepository POST Path={Path}", NewRepositoryPath);

        try
        {
            var dto = await _mediator.Send(
                new AddRepositoryCommand(NewRepositoryPath.Trim()), ct);

            _logger.LogInformation(
                "Index | Repository added — Id={Id} Name={Name}", dto.Id, dto.Name);

            // PRG: redirect with a success flag in TempData to survive the redirect.
            TempData["AddRepoSuccess"] =
                $"'{dto.Name}' is now being tracked. It will be scanned within the next hour.";

            // Redirect to GET to clear the form and prevent re-submission on F5.
            return RedirectToPage();
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex,
                "Index | AddRepository validation failed: {Message}", ex.Message);

            AddRepoError = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            // Thrown by the handler when the path is already tracked.
            _logger.LogWarning(ex,
                "Index | AddRepository conflict: {Message}", ex.Message);

            AddRepoError = ex.Message;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Index | AddRepository unexpected failure for path={Path}",
                NewRepositoryPath);

            AddRepoError =
                "An unexpected error occurred while adding the repository. " +
                "Check the application logs for details.";
        }

        // On error, reload page data and re-render the form (the input value is
        // preserved via [BindProperty] on NewRepositoryPath).
        await OnGetAsync(ct);
        return Page();
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads TempData success message set after a successful PRG redirect.
    /// Called automatically by the Razor Page engine before the page renders.
    /// </summary>
    public override void OnPageHandlerExecuted(
        Microsoft.AspNetCore.Mvc.Filters.PageHandlerExecutedContext context)
    {
        if (TempData.TryGetValue("AddRepoSuccess", out var msg) && msg is string s)
            AddRepoSuccess = s;

        base.OnPageHandlerExecuted(context);
    }
}
