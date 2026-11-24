namespace SalesInventory.Application.Observability;

/// <summary>
/// Métricas de efectos de un handler que solo deben registrarse cuando el trabajo YA está confirmado. Con
/// EnableRetryOnFailure la estrategia puede repetir el efecto completo: el procesador llama a <see cref="Reset"/>
/// antes de cada intento y a <see cref="Flush"/> solo tras el commit, de modo que un reintento interno nunca duplica
/// ni contabiliza un trabajo revertido.
/// </summary>
public sealed class DeferredMetrics
{
    private readonly List<Action> _pending = [];

    public void Reset() => _pending.Clear();

    public void Add(Action record) => _pending.Add(record);

    public void Flush()
    {
        var items = _pending.ToArray();
        _pending.Clear();
        foreach (var record in items)
        {
            record();
        }
    }
}
