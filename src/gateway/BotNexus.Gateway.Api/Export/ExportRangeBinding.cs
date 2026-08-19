using Microsoft.AspNetCore.Mvc;

namespace BotNexus.Gateway.Api.Export;

/// <summary>
/// Route-boundary glue for the partial-range export selector (issue #3279).
/// </summary>
/// <remarks>
/// Shared by the conversation and session export routes so both spell the range query parameters,
/// the "supply both or neither" rule and the rejection-to-status-code mapping identically. Two
/// routes each deciding for themselves is how one endpoint ends up 400-ing a case the other
/// silently accepts.
/// </remarks>
public static class ExportRangeBinding
{
    /// <summary>
    /// Binds the two optional query parameters into a selector.
    /// </summary>
    /// <param name="firstEntryId">The <c>firstEntryId</c> query value.</param>
    /// <param name="lastEntryId">The <c>lastEntryId</c> query value.</param>
    /// <returns>
    /// The selector (<see langword="null"/> when neither parameter was supplied, meaning a full
    /// export) and an error payload when only one of the pair was supplied.
    /// </returns>
    public static (ExportRangeSelector? Range, object? Error) Bind(string? firstEntryId, string? lastEntryId)
    {
        var hasFirst = !string.IsNullOrWhiteSpace(firstEntryId);
        var hasLast = !string.IsNullOrWhiteSpace(lastEntryId);

        if (!hasFirst && !hasLast)
            return (null, null);

        // A half-specified range is rejected rather than completed with an implied transcript
        // start/end. Inferring the missing endpoint would hand back a document covering a range the
        // caller never named, which is the same class of quiet mis-description as clamping.
        if (hasFirst != hasLast)
        {
            return (null, new
            {
                error = "range_incomplete",
                message = "A partial-range export requires both 'firstEntryId' and 'lastEntryId'. " +
                          "The missing endpoint is not inferred from the transcript bounds."
            });
        }

        return (new ExportRangeSelector(firstEntryId!.Trim(), lastEntryId!.Trim()), null);
    }

    /// <summary>
    /// Maps an assembly result onto an HTTP response.
    /// </summary>
    /// <param name="result">The range assembly result.</param>
    /// <param name="format">The requested output format.</param>
    /// <param name="controller">The calling controller.</param>
    /// <returns>The file download on success, 404 for a missing subject, or a specific 400.</returns>
    public static ActionResult ToActionResult(
        ExportRangeResult result,
        ExportFormatKind format,
        ControllerBase controller)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(controller);

        if (result.IsSuccess)
            return ExportResponse.File(result.Document!, format, controller);

        if (result.Error == ExportRangeErrorKind.SubjectNotFound)
            return controller.NotFound();

        // Every remaining reason carries its OWN code and message all the way to the caller. They
        // are deliberately not flattened into one "invalid range" 400: a reversed range, a stale
        // endpoint and an endpoint pasted from a different conversation call for three different
        // corrective actions by the user.
        return controller.BadRequest(new
        {
            error = result.ErrorCode,
            message = result.Message
        });
    }
}
