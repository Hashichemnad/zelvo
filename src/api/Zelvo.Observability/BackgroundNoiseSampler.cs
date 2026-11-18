using System.Diagnostics;
using OpenTelemetry.Trace;

namespace Zelvo.Observability;

/// <summary>
/// Drops outgoing calls that are not part of a request or job: a database query or broker call
/// without a parent span is background polling (for example the search outbox relay every
/// second) and would otherwise make one trace each. Everything else follows <paramref name="inner"/>.
/// Background work worth tracing starts its own span (for example <c>search.index</c>).
/// </summary>
internal sealed class BackgroundNoiseSampler(Sampler inner) : Sampler
{
    public override SamplingResult ShouldSample(in SamplingParameters samplingParameters) =>
        samplingParameters.ParentContext.TraceId == default && samplingParameters.Kind is ActivityKind.Client or ActivityKind.Producer
            ? new SamplingResult(SamplingDecision.Drop)
            : inner.ShouldSample(samplingParameters);
}
