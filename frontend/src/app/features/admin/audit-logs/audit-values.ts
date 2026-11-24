export interface ValueRow {
  field: string;
  oldValue: string | null;
  newValue: string | null;
  changed: boolean;
}

/** Texto visible de un valor; `null` representa «sin valor» (ausente o null), nunca se lanza una excepción. */
export function formatAuditValue(value: unknown): string | null {
  if (value === null || value === undefined) {
    return null;
  }

  if (typeof value === 'string') {
    return value;
  }

  try {
    return typeof value === 'object' ? JSON.stringify(value) : String(value);
  } catch {
    return '[valor no representable]';
  }
}

function asRecord(values: unknown): Record<string, unknown> {
  return typeof values === 'object' && values !== null && !Array.isArray(values) ? (values as Record<string, unknown>) : {};
}

/**
 * Une valores anteriores y nuevos por propiedad (incluidas las desconocidas). Una propiedad se resalta como modificada
 * si el backend la lista en `changedFields` o, en un UPDATE, si sus valores difieren.
 */
export function buildValueRows(oldValues: unknown, newValues: unknown, changedFields: readonly string[], action: string): ValueRow[] {
  const before = asRecord(oldValues);
  const after = asRecord(newValues);
  const fields = [...new Set([...Object.keys(before), ...Object.keys(after)])];

  return fields.map(field => {
    const oldValue = formatAuditValue(before[field]);
    const newValue = formatAuditValue(after[field]);
    const changed = action === 'UPDATE' ? changedFields.includes(field) || oldValue !== newValue : false;
    return { field, oldValue, newValue, changed };
  });
}
