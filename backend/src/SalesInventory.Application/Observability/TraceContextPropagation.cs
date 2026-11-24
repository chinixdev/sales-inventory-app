using System.Diagnostics;

namespace SalesInventory.Application.Observability;

/// <summary>
/// Contexto W3C (traceparent/tracestate) de la operación que originó un evento. Se persiste en el Outbox y el
/// procesamiento asíncrono lo enlaza con un <see cref="ActivityLink"/>: nunca se reutilizan identificadores de spans
/// ajenos. Los eventos anteriores a esta capacidad no tienen contexto y se procesan igual (sin enlace).
/// </summary>
public static class TraceContextPropagation
{
    public const int TraceParentMaxLength = 55;
    public const int TraceStateMaxLength = 512;

    /// <summary>Contexto de la actividad actual, o <c>(null, null)</c> si no hay una actividad W3C.</summary>
    public static (string? TraceParent, string? TraceState) Capture()
    {
        var activity = Activity.Current;
        if (activity is null || activity.IdFormat != ActivityIdFormat.W3C || string.IsNullOrEmpty(activity.Id)
            || activity.Id.Length != TraceParentMaxLength)
        {
            return (null, null);
        }

        var state = activity.TraceStateString;
        return (activity.Id, state is { Length: > 0 and <= TraceStateMaxLength } ? state : null);
    }

    /// <summary>Interpreta un contexto persistido; un valor ausente, antiguo o malformado simplemente no produce enlace.</summary>
    public static bool TryParse(string? traceParent, string? traceState, out ActivityContext context)
    {
        context = default;
        return !string.IsNullOrWhiteSpace(traceParent)
               && traceParent.Length == TraceParentMaxLength
               && ActivityContext.TryParse(traceParent, traceState, isRemote: true, out context);
    }
}
