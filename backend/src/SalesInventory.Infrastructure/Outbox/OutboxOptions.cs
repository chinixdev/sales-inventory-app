namespace SalesInventory.Infrastructure.Outbox;

/// <summary>Configuración del procesamiento del Outbox (sección <c>Outbox</c>).</summary>
public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Espera entre sondeos cuando no hay trabajo.</summary>
    public TimeSpan PollingInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Máximo de mensajes reclamados por lote.</summary>
    public int BatchSize { get; set; } = 20;

    /// <summary>Duración del arrendamiento (lease) de un mensaje reclamado. Debe superar a <see cref="HandlerTimeout"/>.</summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Tiempo máximo para procesar un mensaje (handler + confirmación).</summary>
    public TimeSpan HandlerTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Intentos totales permitidos por mensaje. Cuentan los fallos y también los intentos abandonados
    /// (lease vencido tras una caída). Al agotarse el mensaje pasa a <c>Failed</c>.
    /// </summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Espera base del backoff exponencial.</summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Tope de la espera entre reintentos.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(5);

    public void Validate()
    {
        var errors = new List<string>();

        if (PollingInterval < TimeSpan.FromMilliseconds(50) || PollingInterval > TimeSpan.FromHours(1))
            errors.Add("Outbox:PollingInterval debe estar entre 50 ms y 1 hora.");
        if (BatchSize is < 1 or > 500)
            errors.Add("Outbox:BatchSize debe estar entre 1 y 500.");
        if (LeaseDuration < TimeSpan.FromSeconds(1) || LeaseDuration > TimeSpan.FromHours(1))
            errors.Add("Outbox:LeaseDuration debe estar entre 1 segundo y 1 hora.");
        if (HandlerTimeout <= TimeSpan.Zero)
            errors.Add("Outbox:HandlerTimeout debe ser positivo.");
        if (HandlerTimeout >= LeaseDuration)
            errors.Add("Outbox:HandlerTimeout debe ser menor que Outbox:LeaseDuration para no perder el lease mientras se procesa.");
        if (MaxAttempts is < 1 or > 50)
            errors.Add("Outbox:MaxAttempts debe estar entre 1 y 50.");
        if (BaseRetryDelay <= TimeSpan.Zero)
            errors.Add("Outbox:BaseRetryDelay debe ser positivo.");
        if (MaxRetryDelay < BaseRetryDelay)
            errors.Add("Outbox:MaxRetryDelay no puede ser menor que Outbox:BaseRetryDelay.");
        if (MaxRetryDelay > TimeSpan.FromDays(1))
            errors.Add("Outbox:MaxRetryDelay no puede superar 1 día.");

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Configuración de Outbox inválida: " + string.Join(" ", errors));
        }
    }
}
