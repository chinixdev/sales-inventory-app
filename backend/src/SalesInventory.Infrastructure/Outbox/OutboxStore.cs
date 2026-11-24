using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SalesInventory.Application.Outbox;
using SalesInventory.Domain.Enums;
using SalesInventory.Infrastructure.Persistence;

namespace SalesInventory.Infrastructure.Outbox;

/// <summary>
/// Acceso atómico al Outbox en SQL Server. Cada operación es una sentencia (o una transacción) condicionada a la
/// propiedad Y vigencia del lease (LockId + RowVersion + LockedUntilUtc), de modo que un worker con lease vencido nunca
/// pisa el estado de otro. TODOS los instantes (vencimientos, leases, backoff) usan el reloj UTC de SQL Server
/// (<c>SYSUTCDATETIME()</c>), no el de cada máquina: varias instancias comparten una única referencia de tiempo.
/// Todas son compatibles con <c>EnableRetryOnFailure</c> y seguras de repetir ante un commit incierto.
/// </summary>
public sealed class OutboxStore(SalesInventoryDbContext db, IOptions<OutboxOptions> options)
{
    private readonly OutboxOptions _options = options.Value;

    private static int Milliseconds(TimeSpan value) => (int)Math.Min(value.TotalMilliseconds, int.MaxValue);

    /// <summary>
    /// Pasa a <c>Failed</c> los mensajes cuyo worker cayó (lease vencido) y ya no tienen intentos disponibles.
    /// El intento abandonado cuenta como consumido.
    /// </summary>
    public Task<int> FailExhaustedAbandonedAsync(CancellationToken cancellationToken)
    {
        var maxAttempts = _options.MaxAttempts;
        return db.Database.CreateExecutionStrategy().ExecuteAsync(token => db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE dbo.OutboxMessages
            SET Status = 'Failed',
                RetryCount = RetryCount + 1,
                FailedAtUtc = SYSUTCDATETIME(),
                LockId = NULL,
                LockedUntilUtc = NULL,
                LastError = N'Lease vencido: el procesamiento se abandonó y se agotaron los intentos.'
            WHERE Status = 'Processing'
              AND LockedUntilUtc <= SYSUTCDATETIME()
              AND RetryCount + 1 >= {maxAttempts}", token), cancellationToken);
    }

    /// <summary>
    /// Reclama hasta BatchSize mensajes listos (Pending vencidos o Processing con lease vencido) en UNA sentencia
    /// atómica. UPDLOCK + READPAST hacen que varias instancias salten las filas ya tomadas por otra, así que nadie
    /// reclama el mismo mensaje. Reclamar un lease vencido cuenta un intento abandonado (RetryCount + 1).
    /// La sentencia es idempotente por <paramref name="lockId"/>: si se repite tras un commit incierto devuelve lo
    /// que ya era propio sin contar intentos de más.
    /// </summary>
    public async Task<IReadOnlyList<ClaimedOutboxMessage>> ClaimBatchAsync(Guid lockId, CancellationToken cancellationToken)
    {
        var batchSize = _options.BatchSize;
        var maxAttempts = _options.MaxAttempts;
        var leaseMilliseconds = Milliseconds(_options.LeaseDuration);

        await db.Database.CreateExecutionStrategy().ExecuteAsync(token => db.Database.ExecuteSqlInterpolatedAsync($@"
            WITH candidates AS (
                SELECT TOP ({batchSize}) *
                FROM dbo.OutboxMessages WITH (ROWLOCK, UPDLOCK, READPAST)
                WHERE (Status = 'Pending' AND NextAttemptAtUtc <= SYSUTCDATETIME())
                   OR (Status = 'Processing' AND LockId = {lockId})
                   OR (Status = 'Processing' AND LockedUntilUtc <= SYSUTCDATETIME() AND RetryCount + 1 < {maxAttempts})
                ORDER BY NextAttemptAtUtc, OccurredAtUtc)
            UPDATE candidates
            SET RetryCount = CASE WHEN Status = 'Processing' AND LockId <> {lockId} THEN RetryCount + 1 ELSE RetryCount END,
                Status = 'Processing',
                LockId = {lockId},
                LockedUntilUtc = DATEADD(MILLISECOND, {leaseMilliseconds}, SYSUTCDATETIME())", token), cancellationToken);

        var claimed = await db.OutboxMessages
            .AsNoTracking()
            .Where(message => message.LockId == lockId && message.Status == OutboxMessageStatus.Processing)
            .OrderBy(message => message.NextAttemptAtUtc)
            .ThenBy(message => message.OccurredAtUtc)
            .ToListAsync(cancellationToken);

        return claimed.Select(ClaimedOutboxMessage.From).ToList();
    }

    /// <summary>
    /// Confirma el mensaje y el efecto del consumidor en UNA transacción. Primero valida propiedad Y vigencia del
    /// lease (según el reloj de SQL Server) marcando el mensaje como procesado, lo que además bloquea la fila; si el
    /// lease se perdió o venció se aborta sin ejecutar el efecto. Si el efecto falla, se revierte también la
    /// finalización del mensaje.
    /// </summary>
    /// <exception cref="LeaseLostException">Otro worker reclamó el mensaje o el lease ya venció.</exception>
    public Task CompleteAsync(
        ClaimedOutboxMessage message,
        Func<CancellationToken, Task> effect,
        CancellationToken cancellationToken)
    {
        return db.Database.CreateExecutionStrategy().ExecuteAsync(async token =>
        {
            // Cada intento de la estrategia parte de un estado limpio.
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(token);

            var updated = await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE dbo.OutboxMessages
                SET Status = 'Processed',
                    ProcessedAtUtc = SYSUTCDATETIME(),
                    LastError = NULL,
                    LockId = NULL,
                    LockedUntilUtc = NULL
                WHERE Id = {message.Id}
                  AND Status = 'Processing'
                  AND LockId = {message.LockId}
                  AND RowVersion = {message.RowVersion}
                  AND LockedUntilUtc > SYSUTCDATETIME()", token);

            if (updated == 0)
            {
                // Commit incierto de un intento anterior de ESTE worker: el mensaje ya quedó Processed junto con su
                // efecto (misma transacción), no hay nada que repetir. Cualquier otro estado es un lease perdido.
                var alreadyProcessed = await db.OutboxMessages
                    .AsNoTracking()
                    .AnyAsync(m => m.Id == message.Id && m.Status == OutboxMessageStatus.Processed, token);
                if (alreadyProcessed)
                {
                    return;
                }

                throw new LeaseLostException(message.Id);
            }

            await effect(token);
            await transaction.CommitAsync(token);
        }, cancellationToken);
    }

    /// <summary>
    /// Registra un intento fallido: reintento con backoff (Pending) o Failed si el error es permanente o se
    /// agotaron los intentos. Solo actúa si el worker sigue siendo dueño del lease.
    /// </summary>
    /// <returns><c>false</c> si el lease ya no era propio.</returns>
    public async Task<bool> RecordFailureAsync(
        ClaimedOutboxMessage message,
        string error,
        bool permanent,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        var attemptsUsed = message.RetryCount + 1;
        var exhausted = permanent || attemptsUsed >= _options.MaxAttempts;
        var status = (exhausted ? OutboxMessageStatus.Failed : OutboxMessageStatus.Pending).ToString();
        var delayMilliseconds = Milliseconds(retryDelay);
        var safeError = OutboxErrorFormatter.Sanitize(error);

        var updated = await db.Database.CreateExecutionStrategy().ExecuteAsync(token => db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE dbo.OutboxMessages
            SET RetryCount = {attemptsUsed},
                Status = {status},
                NextAttemptAtUtc = DATEADD(MILLISECOND, {delayMilliseconds}, SYSUTCDATETIME()),
                FailedAtUtc = CASE WHEN {status} = 'Failed' THEN SYSUTCDATETIME() ELSE NULL END,
                LastError = {safeError},
                LockId = NULL,
                LockedUntilUtc = NULL
            WHERE Id = {message.Id}
              AND Status = 'Processing'
              AND LockId = {message.LockId}
              AND RowVersion = {message.RowVersion}", token), cancellationToken);

        return updated > 0;
    }

    /// <summary>Devuelve el mensaje a Pending sin consumir un intento (p. ej. apagado ordenado antes de procesarlo).</summary>
    public async Task<bool> ReleaseAsync(ClaimedOutboxMessage message, CancellationToken cancellationToken)
    {
        var updated = await db.Database.CreateExecutionStrategy().ExecuteAsync(token => db.Database.ExecuteSqlInterpolatedAsync($@"
            UPDATE dbo.OutboxMessages
            SET Status = 'Pending',
                LockId = NULL,
                LockedUntilUtc = NULL
            WHERE Id = {message.Id}
              AND Status = 'Processing'
              AND LockId = {message.LockId}
              AND RowVersion = {message.RowVersion}", token), cancellationToken);

        return updated > 0;
    }
}
