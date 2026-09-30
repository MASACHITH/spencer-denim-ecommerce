/**
 * One `ops.AuditLogs` row (ADM-03, doc 08 §3.8), exactly as `GetAuditLogsAsync` returns it. Before/after
 * JSON was already redacted when the row was written (DB24, the audit interceptor) — this screen only
 * ever renders what was already safe to store, and renders it as text, never as evaluated markup.
 */
export interface AuditLogEntry {
  readonly id: number;
  readonly occurredAtUtc: string;
  readonly userId: number | null;
  readonly userEmail: string | null;
  readonly action: string;
  readonly entityType: string;
  readonly entityId: string | null;
  readonly beforeJson: string | null;
  readonly afterJson: string | null;
  readonly ipAddress: string | null;
  readonly userAgent: string | null;
  readonly correlationId: string | null;
}

/** One page of a listing, the same shape the catalogue listing uses (`PagedResult<T>`, doc 08 §7.1). */
export interface PagedResult<TItem> {
  readonly items: readonly TItem[];
  readonly page: number;
  readonly pageSize: number;
  readonly totalCount: number;
}

/** The filters the audit log screen offers, matching `GetAuditLogsQuery` field for field. */
export interface AuditLogFilters {
  entityType: string;
  entityId: string;
  userId: string;
  action: string;
  fromUtc: string;
  toUtc: string;
}

export function emptyFilters(): AuditLogFilters {
  return { entityType: '', entityId: '', userId: '', action: '', fromUtc: '', toUtc: '' };
}

/**
 * The RFC 9457 body `GetAuditLogsAsync` returns for a rejected query string, keyed by the query-string
 * parameter name (`ToValidationProblem` in `AuditLogEndpoints`) — the same shape the catalogue listing's
 * `ValidationProblemDetails` uses.
 */
export interface ValidationProblemDetails {
  readonly title?: string;
  readonly errors?: Record<string, string[]>;
}

/**
 * The fixed `ops.AuditLogs.Action` vocabulary (doc 08 §3.8, `SpencerDenim.Domain.Auditing.AuditActions`),
 * offered as a dropdown so a filter can be picked instead of typed. The API itself does not restrict the
 * value to this list (new actions may be added over time), so this is a convenience, not a hard rule.
 */
export const AUDIT_ACTIONS: readonly string[] = [
  'Create',
  'Update',
  'SoftDelete',
  'Restore',
  'StatusChange',
  'Approve',
  'Refund',
  'Export',
  'Login',
  'LoginFailed',
  'Lockout',
  'PermissionChange',
  'SettingChange',
  'LinkGuestOrders',
  'Anonymise',
  'StaffDeactivated',
  'StaffReactivated',
  'EmailChanged',
  'AdminPasswordReset',
  'TwoFactorReset',
  'RoleAssigned',
  'RoleUnassigned',
  'BreakGlass',
  'AlertAcknowledged',
  'PasswordChanged',
  'PasswordChangeFailed',
];
