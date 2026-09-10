using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;

namespace ServiceMantle.AspNetCore.Http;

internal sealed class PipelineComposition
{
    private static readonly ConditionalWeakTable<IApplicationBuilder, PipelineComposition> States = new();
    private bool used;
    private bool composing;
    private bool completed;

    internal static void Begin(IApplicationBuilder app)
    {
        var state = States.GetValue(app, static _ => new PipelineComposition());
        if (state.used || state.composing || state.completed) throw Failure();
        state.composing = true;
    }

    internal static void RecordUse(IApplicationBuilder app)
    {
        var state = States.GetValue(app, static _ => new PipelineComposition());
        if (state.completed) throw Failure();
        state.used = true;
    }

    /// <summary>
    /// Reports whether the composed pipeline already ran on this builder, without creating state
    /// for a builder that has none.
    /// </summary>
    internal static bool IsCompleted(IApplicationBuilder app) =>
        States.TryGetValue(app, out var state) && state.completed;

    internal static void Complete(IApplicationBuilder app)
    {
        var state = States.GetValue(app, static _ => new PipelineComposition());
        state.composing = false;
        state.completed = true;
    }

    private static InvalidOperationException Failure() =>
        new("The ServiceMantle pipeline cannot be repeated or mixed with its individual middleware entry points.");
}
