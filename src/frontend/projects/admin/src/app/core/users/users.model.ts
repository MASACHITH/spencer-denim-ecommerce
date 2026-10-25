/** One role, as referenced from a staff user (ADM-02, W1-08). */
export interface RoleRef {
  readonly id: number;
  readonly name: string;
  readonly grantsAllPermissions: boolean;
}

/** One row of the staff user list, exactly as `GetStaffUsersAsync` returns it. */
export interface StaffUserSummary {
  readonly id: number;
  readonly email: string;
  readonly firstName: string;
  readonly lastName: string | null;
  readonly isActive: boolean;
  readonly mustChangePassword: boolean;
  readonly lastLoginAtUtc: string | null;
  readonly roles: readonly RoleRef[];
  /** Named in the server's protected list: it can never be deactivated, so the screen offers no button. */
  readonly isProtected: boolean;
  /**
   * A guarded account (the client's own login) and the person looking is not the technical owner's account: they
   * cannot deactivate, reactivate or rename it, so the screen offers no button.
   */
  readonly lockedForViewer: boolean;
}

/** One staff user's detail, including the effective permissions computed from their roles (doc 08 §3.1 R4). */
export interface StaffUserDetail {
  readonly id: number;
  readonly email: string;
  readonly firstName: string;
  readonly lastName: string | null;
  readonly isActive: boolean;
  readonly mustChangePassword: boolean;
  readonly lastLoginAtUtc: string | null;
  readonly createdAtUtc: string;
  readonly grantsAllPermissions: boolean;
  readonly roles: readonly RoleRef[];
  readonly effectivePermissions: readonly string[];
  /** As on the list row: it can never be deactivated, and its email address cannot be changed. */
  readonly isProtected: boolean;
  /** As on the list row: only the technical owner's account can activate, deactivate or rename it. */
  readonly lockedForViewer: boolean;
}

/** One row of the role list. Role management is read-only in this slice (doc 08 §3.1 DB4 rule 1). */
export interface RoleSummary {
  readonly id: number;
  readonly name: string;
  readonly description: string | null;
  readonly isSystem: boolean;
  readonly grantsAllPermissions: boolean;
  readonly permissionCount: number;
  readonly userCount: number;
}

/** One role's full permission set. */
export interface RoleDetail {
  readonly id: number;
  readonly name: string;
  readonly description: string | null;
  readonly isSystem: boolean;
  readonly grantsAllPermissions: boolean;
  readonly permissions: readonly string[];
  readonly userCount: number;
}

/** One page of a listing, the same shape the catalogue listing and audit log viewer use. */
export interface PagedResult<TItem> {
  readonly items: readonly TItem[];
  readonly page: number;
  readonly pageSize: number;
  readonly totalCount: number;
}

/** The filters the staff user list offers, matching `GetStaffUsersQuery` field for field. */
export interface StaffUserListFilters {
  search: string;
  roleId: string;
  isActive: string;
}

export function emptyStaffUserListFilters(): StaffUserListFilters {
  return { search: '', roleId: '', isActive: '' };
}

/** The RFC 9457 body a rejected query string or request body comes back as, keyed by field name. */
export interface ValidationProblemDetails {
  readonly title?: string;
  readonly errors?: Record<string, string[]>;
}

/** Body of `POST /api/admin/users`. */
export interface CreateStaffUserRequest {
  readonly email: string;
  readonly firstName: string;
  readonly lastName: string | null;
  readonly initialPassword: string;
  readonly roleIds: readonly number[];
}

/** Body of `PUT /api/admin/users/{id}`. `email` is omitted (not sent as null) when it is not changing. */
export interface UpdateStaffUserProfileRequest {
  readonly firstName: string;
  readonly lastName: string | null;
  readonly email?: string;
}

/** Body of `PUT /api/admin/users/{id}/roles`. */
export interface AssignUserRolesRequest {
  readonly roleIds: readonly number[];
}
