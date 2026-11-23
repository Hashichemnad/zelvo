using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Zelvo.Api.Identity;
using Zelvo.Data;
using Zelvo.Data.Auditing;
using Zelvo.Data.Integrations;
using Zelvo.Data.Tenancy;
using Zelvo.Integrations;

namespace Zelvo.Api.Integrations;

/// <summary>
/// For every API call made by an integration: writes the integration log (method, path with query
/// values masked, status, timing, request and response bodies with personal data masked, cut to
/// 16 KB) and an audit entry (module 14: "every call is audit-logged"). Both are written in their
/// own scope after the response, so a failed call is logged too and the call's own changes are
/// never saved by accident.
/// </summary>
internal sealed partial class IntegrationLogMiddleware(RequestDelegate next, IServiceScopeFactory scopes, ILogger<IntegrationLogMiddleware> logger)
{
    private const int MaxCapturedBytes = 64 * 1024;

    public async Task InvokeAsync(HttpContext context, CurrentUser currentUser, ITenantContext tenant)
    {
        if (currentUser.ApiClient is not { } client || tenant.TenantId is not { } tenantId || tenant.Slug is not { } slug)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var started = DateTimeOffset.UtcNow;
        var watch = Stopwatch.StartNew();
        var requestBody = await ReadRequestAsync(context.Request).ConfigureAwait(false);

        var originalBody = context.Response.Body;
        var capture = new CaptureStream(originalBody, IntegrationLogEntry.BodyMaxLength);
        context.Response.Body = capture;
        string? error = null;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name;
            throw;
        }
        finally
        {
            context.Response.Body = originalBody;
            var status = error is null ? context.Response.StatusCode : StatusCodes.Status500InternalServerError;
            try
            {
                await WriteAsync(context, client, tenantId, slug, started, (int)watch.ElapsedMilliseconds, status, requestBody, capture.Captured, error).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Logging must not change the answer; the failure is visible in the service logs.
                LogWriteFailed(logger, ex);
            }
        }
    }

    private async Task WriteAsync(
        HttpContext context, ApiClient client, Guid tenantId, string slug, DateTimeOffset started, int durationMs, int status,
        string? requestBody, string responseBody, string? error)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetTenant(tenantId, slug);
        var audit = scope.ServiceProvider.GetRequiredService<AuditContext>();
        audit.SetActor(AuditActorType.ApiClient, client.Id.ToString(), client.Name);
        var correlationId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        audit.SetMetadata("correlationId", correlationId);
        audit.SetMetadata("clientAddress", context.Connection.RemoteIpAddress?.ToString());
        audit.SetMetadata("userAgent", context.Request.Headers.UserAgent.ToString());

        var request = context.Request;
        var path = request.Path.Value ?? "/";
        var target = path + PersonalDataMasker.MaskQueryString(request.QueryString.Value ?? string.Empty);
        var db = scope.ServiceProvider.GetRequiredService<ZelvoDbContext>();
        db.IntegrationLog.Add(new IntegrationLogEntry
        {
            OccurredAt = started,
            Direction = IntegrationDirection.Incoming,
            ApiClientId = client.Id,
            Method = request.Method,
            Target = target.Length <= IntegrationLogEntry.TargetMaxLength ? target : target[..IntegrationLogEntry.TargetMaxLength],
            StatusCode = status,
            DurationMs = durationMs,
            RequestBody = PersonalDataMasker.MaskBody(requestBody, request.ContentType, IntegrationLogEntry.BodyMaxLength),
            ResponseBody = IsText(context.Response.ContentType)
                ? PersonalDataMasker.MaskBody(responseBody, context.Response.ContentType, IntegrationLogEntry.BodyMaxLength)
                : null,
            Error = error,
            CorrelationId = correlationId.Length <= IntegrationLogEntry.CorrelationIdMaxLength ? correlationId : correlationId[..IntegrationLogEntry.CorrelationIdMaxLength],
        });

        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? path;
        scope.ServiceProvider.GetRequiredService<IAuditLog>().Add(AuditCategory.Security, "integrations.api.called", "api_client", client.Id.ToString(),
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["method"] = request.Method,
                ["route"] = route,
                ["status"] = status,
                ["kind"] = client.Kind.ToString(),
            });
        await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);

        // Last use, at most every five minutes; not an audited change.
        var now = DateTimeOffset.UtcNow;
        if (client.LastUsedAt is null || now - client.LastUsedAt > TimeSpan.FromMinutes(5))
        {
            await db.Database.ExecuteSqlAsync($"UPDATE api_clients SET last_used_at = {now} WHERE id = {client.Id}", CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<string?> ReadRequestAsync(HttpRequest request)
    {
        if (request.ContentLength is null or 0 or > MaxCapturedBytes || !IsText(request.ContentType))
        {
            return null;
        }

        request.EnableBuffering();
        var buffer = new byte[(int)request.ContentLength.Value];
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await request.Body.ReadAsync(buffer.AsMemory(read), request.HttpContext.RequestAborted).ConfigureAwait(false);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        request.Body.Position = 0;
        return Encoding.UTF8.GetString(buffer, 0, read);
    }

    private static bool IsText(string? contentType) =>
        contentType is not null
        && (contentType.Contains("json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("form-urlencoded", StringComparison.OrdinalIgnoreCase)
            || contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase));

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not write the integration log for an API call.")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception);

    /// <summary>Passes the response through and keeps its first characters (text responses only).</summary>
    private sealed class CaptureStream(Stream inner, int maxChars) : Stream
    {
        private readonly MemoryStream _buffer = new();

        public string Captured => _buffer.Length == 0 ? string.Empty : Encoding.UTF8.GetString(_buffer.GetBuffer(), 0, (int)_buffer.Length);

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Keep(buffer.AsSpan(offset, count));
            inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Keep(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private void Keep(ReadOnlySpan<byte> bytes)
        {
            // Bytes, not characters, but UTF-8 text is never shorter in bytes; the masker cuts again.
            var room = (maxChars * 2) - (int)_buffer.Length;
            if (room > 0)
            {
                _buffer.Write(bytes[..Math.Min(room, bytes.Length)]);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _buffer.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
