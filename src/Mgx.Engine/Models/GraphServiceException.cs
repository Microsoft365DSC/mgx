using System.Net;
using System.Text.Json;

namespace Mgx.Engine.Models;

/// <summary>
/// Exception thrown when the Graph API returns an error response.
/// Parses the { "error": { "code": "...", "message": "..." } } body.
/// </summary>
public class GraphServiceException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public string? ErrorCode { get; }

    public string? InnerErrorMessage { get; }

    public GraphServiceException(HttpStatusCode statusCode, string responseBody)
        : base(FormatAndExtract(statusCode, responseBody, out var code, out var innerMessage))
    {
        StatusCode = statusCode;
        ErrorCode = code;
        InnerErrorMessage = innerMessage;
    }

    private static string FormatAndExtract(HttpStatusCode statusCode, string responseBody, out string? errorCode, out string? innerErrorMessage)
    {
        errorCode = null;
        innerErrorMessage = null;
        if (string.IsNullOrEmpty(responseBody))
            return $"HTTP {(int)statusCode}: {statusCode}";

        try
        {
            using var doc = JsonDocument.Parse(responseBody);

            // Shape-check before every access. TryGetProperty and GetString throw on the wrong
            // kind, and neither is a JsonException, so unexpected JSON would escape as an
            // exception thrown from inside an exception constructor. Graph emits the OData
            // envelope, but the content path second hop talks to hosts that are not Graph
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return $"HTTP {(int)statusCode}: {statusCode}";

            if (doc.RootElement.TryGetProperty("error", out var errorObj))
            {
                if (errorObj.ValueKind != JsonValueKind.Object)
                    return $"HTTP {(int)statusCode}: {statusCode}";

                var code = AsString(errorObj, "code");
                var message = AsString(errorObj, "message");
                errorCode = code;
                innerErrorMessage = ExtractInnerErrorMessage(errorObj);

                // Build formatted message from whatever Graph provided
                var formatted = !string.IsNullOrEmpty(code)
                    ? $"{code}: {message}"
                    : !string.IsNullOrEmpty(message)
                        ? message
                        : $"HTTP {(int)statusCode}: {statusCode}";

                if (!string.IsNullOrEmpty(innerErrorMessage)
                    && !string.Equals(innerErrorMessage, message, StringComparison.Ordinal))
                {
                    formatted += $"\n{innerErrorMessage}";
                }

                var guidance = GetGuidanceForCode(code);
                if (guidance != null)
                    formatted += $"\nHint: {guidance}";
                return formatted;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Backstop: the shape guards above cover the reachable cases, but an error body must
            // never be able to throw out of here - callers are constructing an exception.
        }
        return $"HTTP {(int)statusCode}: {statusCode}";
    }

    private static string? ExtractInnerErrorMessage(JsonElement errorObj)
    {
        string? message = null;
        var current = errorObj;

        for (var depth = 0; depth < 8; depth++)
        {
            if (!current.TryGetProperty("innerError", out var inner) || inner.ValueKind != JsonValueKind.Object)
            {
                break;
            }

            var innerMessage = AsString(inner, "message");
            if (!string.IsNullOrWhiteSpace(innerMessage))
            {
                message = innerMessage.Trim();
            }

            current = inner;
        }

        return message;
    }

    private static string? AsString(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();

        // Some endpoints nest it as { "value": "..." } - the very shape this helper's own
        // comment names. Returning null there produced "Code: " with an empty message, which
        // is strictly worse than the text that was sitting one level down.
        if (v.ValueKind == JsonValueKind.Object
            && v.TryGetProperty("value", out var inner)
            && inner.ValueKind == JsonValueKind.String)
        {
            return inner.GetString();
        }
        return null;
    }

    /// <summary>
    /// Maps common Graph error codes to user-facing guidance strings.
    /// Returns null for unrecognized codes.
    /// </summary>
    internal static string? GetGuidanceForCode(string? errorCode) => errorCode switch
    {
        "Authorization_RequestDenied" => "Check your Graph scopes with Get-MgContext. The required permission may not be consented.",
        "Request_ResourceNotFound" => "Verify the URI path and that the resource exists. Use -SkipNotFound to suppress in fan-out.",
        "Request_BadRequest" => "Check $filter syntax, property names, and $search quoting. Use -ConsistencyLevel eventual for advanced queries.",
        "InvalidAuthenticationToken" => "Session may have expired. Run Connect-MgGraph to re-authenticate.",
        "Authentication_ExpiredToken" => "Token has expired. Run Connect-MgGraph to re-authenticate.",
        "ErrorAccessDenied" => "Insufficient permissions. Check required scopes at https://learn.microsoft.com/graph/permissions-reference.",
        "Forbidden" => "Access denied. This may require admin consent or an application permission (not delegated).",
        "TooManyRequests" or "activityLimitReached" => "Throttled by Graph API. Mgx handles this automatically; increase -TotalTimeoutSeconds if retries are exhausted.",
        "ServiceNotAvailable" => "Graph service is temporarily unavailable. Mgx retries automatically; check https://status.cloud.microsoft.com for outages.",
        "BadRequest" => "Malformed request. Check $filter, $select, $orderby syntax and property name spelling.",
        _ => null
    };
}
