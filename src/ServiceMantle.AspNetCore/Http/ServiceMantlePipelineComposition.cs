using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;

namespace ServiceMantle.AspNetCore;

internal sealed class ServiceMantlePipelineComposition
{
    private static readonly ConditionalWeakTable<IApplicationBuilder, ServiceMantlePipelineComposition> States = new();
    private bool used;
    private bool composing;
    private bool completed;

    internal static void Begin(IApplicationBuilder app)
    {
        var state = States.GetValue(app, static _ => new ServiceMantlePipelineComposition());
        if (state.used || state.composing || state.completed) throw Failure();
        state.composing = true;
    }

    internal static void RecordUse(IApplicationBuilder app)
    {
        var state = States.GetValue(app, static _ => new ServiceMantlePipelineComposition());
        if (state.completed) throw Failure();
        state.used = true;
    }

    internal static void Complete(IApplicationBuilder app)
    {
        var state = States.GetValue(app, static _ => new ServiceMantlePipelineComposition());
        state.composing = false;
        state.completed = true;
    }

    private static InvalidOperationException Failure() =>
        new("The ServiceMantle pipeline cannot be repeated or mixed with its individual middleware entry points.");
}
