using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Zelvo.Api.Conventions;

namespace Zelvo.Api.Tests;

/// <summary>Problems written by middleware (rate limits, tenant resolution) are always written.</summary>
public sealed class ApiProblemTests
{
    [Theory]
    [InlineData("application/json")]
    [InlineData("text/event-stream")]
    [InlineData("text/html")]
    [InlineData(null)]
    public async Task Middleware_problems_are_written_whatever_the_request_accepts(string? accept)
    {
        var services = new ServiceCollection().AddLogging().AddProblemDetails(o => o.CustomizeProblemDetails = ApiProblem.Customize).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        if (accept is not null)
        {
            context.Request.Headers.Accept = accept;
        }

        using var body = new MemoryStream();
        context.Response.Body = body;

        await ApiProblem.WriteAsync(context, StatusCodes.Status429TooManyRequests, "rate-limited", "Too many requests.");

        Assert.Equal(StatusCodes.Status429TooManyRequests, context.Response.StatusCode);
        Assert.StartsWith("application/problem+json", context.Response.ContentType, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(body.ToArray());
        Assert.Equal("rate-limited", json.RootElement.GetProperty("code").GetString());
        Assert.Equal(429, json.RootElement.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("traceId").GetString()));
    }
}
