namespace Mgx.E2ETests.Infrastructure;

/// <summary>
/// Stub mappings are registered at runtime rather than mounted as files because Testcontainers
/// assigns the HTTPS port randomly and every nextLink has to carry that exact authority, or
/// NextLinkValidator drops it.
/// </summary>
public static class GraphStubs
{
    public static string Get(string path, string body, string? queryKey = null, string? queryValue = null)
    {
        var query = queryKey is null
            ? ""
            : $$""" , "queryParameters": { "{{queryKey}}": {{(queryValue is null
                    ? """{ "absent": true }"""
                    : $$"""{ "equalTo": "{{queryValue}}" }""")}} } """;

        return $$"""
        {
          "priority": 1,
          "request": { "method": "GET", "urlPath": "{{path}}"{{query}} },
          "response": {
            "status": 200,
            "headers": { "Content-Type": "application/json" },
            "jsonBody": {{body}}
          }
        }
        """;
    }

    public static string Status(string method, string path, int status, string body)
        => $$"""
        {
          "priority": 1,
          "request": { "method": "{{method}}", "urlPath": "{{path}}" },
          "response": {
            "status": {{status}},
            "headers": { "Content-Type": "application/json" },
            "jsonBody": {{body}}
          }
        }
        """;

    /// <summary>One step of a scenario, which lets the same URL answer differently on each call.</summary>
    public static string Step(string method, string path, string scenario, string fromState,
        string? toState, int status, string body)
    {
        var transition = toState is null ? "" : $$""" "newScenarioState": "{{toState}}", """;
        return $$"""
        {
          "scenarioName": "{{scenario}}",
          "requiredScenarioState": "{{fromState}}",
          {{transition}}
          "request": { "method": "{{method}}", "urlPath": "{{path}}" },
          "response": {
            "status": {{status}},
            "headers": { "Content-Type": "application/json" },
            "jsonBody": {{body}}
          }
        }
        """;
    }

    public static string Users(params string[] ids)
        => $$"""{ "value": [ {{string.Join(",", ids.Select(i => $$"""{ "id": "{{i}}", "displayName": "{{i}}" }"""))}} ] }""";

    public static string UsersWithNext(string nextLink, params string[] ids)
        => $$"""{ "@odata.nextLink": "{{nextLink}}", "value": [ {{string.Join(",", ids.Select(i => $$"""{ "id": "{{i}}" }"""))}} ] }""";

    public static string BatchResponses(params string[] items)
        => $$"""{ "responses": [ {{string.Join(",", items)}} ] }""";

    public static string BatchItem(int id, int status, string body = "{}")
        => $$"""{ "id": "{{id}}", "status": {{status}}, "headers": { "Content-Type": "application/json" }, "body": {{body}} }""";
}
