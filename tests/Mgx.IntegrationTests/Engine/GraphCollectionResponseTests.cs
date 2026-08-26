using System.Text.Json;
using System.Text.Json.Serialization;
using Mgx.Engine.Models;

namespace Mgx.IntegrationTests.Engine;

public class GraphCollectionResponseTests
{
    [Fact]
    public void Deserialize_ValueArray()
    {
        var json = """{"value":[{"id":"1"},{"id":"2"}]}""";
        var response = JsonSerializer.Deserialize<GraphCollectionResponse<TestItem>>(json);

        Assert.NotNull(response);
        Assert.Equal(2, response!.Value.Length);
        Assert.Equal("1", response.Value[0].Id);
        Assert.Equal("2", response.Value[1].Id);
    }

    [Fact]
    public void Deserialize_NextLink()
    {
        var json = """{"value":[{"id":"1"}],"@odata.nextLink":"https://graph.microsoft.com/v1.0/users?$skiptoken=abc"}""";
        var response = JsonSerializer.Deserialize<GraphCollectionResponse<TestItem>>(json);

        Assert.NotNull(response);
        Assert.Equal("https://graph.microsoft.com/v1.0/users?$skiptoken=abc", response!.NextLink);
    }

    [Fact]
    public void Deserialize_Count()
    {
        var json = """{"value":[{"id":"1"}],"@odata.count":42}""";
        var response = JsonSerializer.Deserialize<GraphCollectionResponse<TestItem>>(json);

        Assert.NotNull(response);
        Assert.Equal(42L, response!.Count);
    }

    [Fact]
    public void Deserialize_Context()
    {
        var json = """{"value":[{"id":"1"}],"@odata.context":"https://graph.microsoft.com/v1.0/$metadata#users"}""";
        var response = JsonSerializer.Deserialize<GraphCollectionResponse<TestItem>>(json);

        Assert.NotNull(response);
        Assert.Equal("https://graph.microsoft.com/v1.0/$metadata#users", response!.Context);
    }

    [Fact]
    public void Deserialize_AllProperties()
    {
        var json = """
            {
              "value": [{"id":"1"},{"id":"2"}],
              "@odata.nextLink": "https://graph.microsoft.com/v1.0/users?$skiptoken=xyz",
              "@odata.count": 100,
              "@odata.context": "https://graph.microsoft.com/v1.0/$metadata#users"
            }
            """;
        var response = JsonSerializer.Deserialize<GraphCollectionResponse<TestItem>>(json);

        Assert.NotNull(response);
        Assert.Equal(2, response!.Value.Length);
        Assert.Equal("https://graph.microsoft.com/v1.0/users?$skiptoken=xyz", response.NextLink);
        Assert.Equal(100L, response.Count);
        Assert.Equal("https://graph.microsoft.com/v1.0/$metadata#users", response.Context);
    }

    [Fact]
    public void Deserialize_EmptyValueArray()
    {
        var json = """{"value":[]}""";
        var response = JsonSerializer.Deserialize<GraphCollectionResponse<TestItem>>(json);

        Assert.NotNull(response);
        Assert.Empty(response!.Value);
    }

    [Fact]
    public void Deserialize_NullNextLink()
    {
        var json = """{"value":[{"id":"1"}],"@odata.nextLink":null}""";
        var response = JsonSerializer.Deserialize<GraphCollectionResponse<TestItem>>(json);

        Assert.NotNull(response);
        Assert.Null(response!.NextLink);
    }

    [Fact]
    public void Deserialize_NullCount()
    {
        var json = """{"value":[{"id":"1"}],"@odata.count":null}""";
        var response = JsonSerializer.Deserialize<GraphCollectionResponse<TestItem>>(json);

        Assert.NotNull(response);
        Assert.Null(response!.Count);
    }

    [Fact]
    public void Deserialize_NullContext()
    {
        var json = """{"value":[{"id":"1"}],"@odata.context":null}""";
        var response = JsonSerializer.Deserialize<GraphCollectionResponse<TestItem>>(json);

        Assert.NotNull(response);
        Assert.Null(response!.Context);
    }

    [Fact]
    public void Deserialize_MissingProperties_DefaultsToNullOrEmpty()
    {
        var json = """{"value":[{"id":"1"}]}""";
        var response = JsonSerializer.Deserialize<GraphCollectionResponse<TestItem>>(json);

        Assert.NotNull(response);
        Assert.Single(response!.Value);
        Assert.Null(response.NextLink);
        Assert.Null(response.Count);
        Assert.Null(response.Context);
    }

    [Fact]
    public void GraphRawCollectionResponse_Deserialize_ValueArray()
    {
        var json = """{"value":[{"id":"1"},{"id":"2"}]}""";
        var response = JsonSerializer.Deserialize<GraphRawCollectionResponse>(json);

        Assert.NotNull(response);
        Assert.Equal(2, response!.Value.Length);
        Assert.Equal("1", response.Value[0].GetProperty("id").GetString());
        Assert.Equal("2", response.Value[1].GetProperty("id").GetString());
    }

    [Fact]
    public void GraphRawCollectionResponse_Deserialize_DeltaLink()
    {
        var json = """{"value":[{"id":"1"}],"@odata.deltaLink":"https://graph.microsoft.com/v1.0/users/delta?$deltatoken=abc"}""";
        var response = JsonSerializer.Deserialize<GraphRawCollectionResponse>(json);

        Assert.NotNull(response);
        Assert.Equal("https://graph.microsoft.com/v1.0/users/delta?$deltatoken=abc", response!.DeltaLink);
    }

    [Fact]
    public void GraphRawCollectionResponse_Deserialize_AllProperties()
    {
        var json = """
            {
              "value": [{"id":"1"}],
              "@odata.nextLink": "https://graph.microsoft.com/v1.0/users?$skiptoken=xyz",
              "@odata.count": 50,
              "@odata.deltaLink": "https://graph.microsoft.com/v1.0/users/delta?$deltatoken=abc"
            }
            """;
        var response = JsonSerializer.Deserialize<GraphRawCollectionResponse>(json);

        Assert.NotNull(response);
        Assert.Single(response!.Value);
        Assert.Equal("https://graph.microsoft.com/v1.0/users?$skiptoken=xyz", response.NextLink);
        Assert.Equal(50L, response.Count);
        Assert.Equal("https://graph.microsoft.com/v1.0/users/delta?$deltatoken=abc", response.DeltaLink);
    }

    [Fact]
    public void GraphBatchRequest_SerializesCorrectly()
    {
        var request = new GraphBatchRequest
        {
            Requests =
            [
                new GraphBatchRequestItem
                {
                    Id = "1",
                    Method = "GET",
                    Url = "/users",
                    Headers = new Dictionary<string, string> { { "ConsistencyLevel", "eventual" } }
                },
                new GraphBatchRequestItem
                {
                    Id = "2",
                    Method = "POST",
                    Url = "/users",
                    Body = JsonDocument.Parse("""{"displayName":"Test"}""").RootElement
                }
            ]
        };

        var json = JsonSerializer.Serialize(request);

        Assert.Contains("\"id\":\"1\"", json);
        Assert.Contains("\"method\":\"GET\"", json);
        Assert.Contains("\"url\":\"/users\"", json);
        Assert.Contains("\"ConsistencyLevel\":\"eventual\"", json);
        Assert.Contains("\"method\":\"POST\"", json);
        Assert.Contains("\"displayName\":\"Test\"", json);
    }

    [Fact]
    public void GraphBatchResponse_DeserializeCorrectly()
    {
        var json = """
            {
              "responses": [
                { "id": "1", "status": 200, "body": { "id": "user1" } },
                { "id": "2", "status": 404, "body": { "error": { "code": "NotFound" } } }
              ]
            }
            """;
        var response = JsonSerializer.Deserialize<GraphBatchResponse>(json);

        Assert.NotNull(response);
        Assert.Equal(2, response!.Responses.Count);
        Assert.Equal("1", response.Responses[0].Id);
        Assert.Equal(200, response.Responses[0].Status);
        Assert.Equal("user1", response.Responses[0].Body!.Value.GetProperty("id").GetString());
        Assert.Equal("2", response.Responses[1].Id);
        Assert.Equal(404, response.Responses[1].Status);
        Assert.Equal("NotFound", response.Responses[1].Body!.Value.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public void BatchOperation_RecordEquality()
    {
        var op1 = new BatchOperation("/users", "GET", null);
        var op2 = new BatchOperation("/users", "GET", null);
        var op3 = new BatchOperation("/users", "POST", null);

        Assert.Equal(op1, op2);
        Assert.NotEqual(op1, op3);
        Assert.Equal(op1.GetHashCode(), op2.GetHashCode());
    }

    [Fact]
    public void BatchOperation_WithBody_Equality()
    {
        var body = JsonDocument.Parse("""{"id":"1"}""").RootElement;
        var op1 = new BatchOperation("/users", "POST", body);
        var op2 = new BatchOperation("/users", "POST", body);
        var op3 = new BatchOperation("/users", "POST", JsonDocument.Parse("""{"id":"2"}""").RootElement);

        Assert.Equal(op1, op2);
        Assert.NotEqual(op1, op3);
    }

    private sealed class TestItem
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;
    }
}