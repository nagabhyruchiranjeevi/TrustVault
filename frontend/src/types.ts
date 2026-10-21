export interface User {
  id: string;
  email: string;
  fullName: string;
  roles: string[];
  createdAtUtc: string;
}

export interface AuthResponse {
  token: string;
  expiresAtUtc: string;
  user: User;
}

export interface DocumentItem {
  id: string;
  title: string;
  fileName: string;
  contentType: string;
  fileSizeBytes: number;
  ownerId: string;
  ownerEmail?: string;
  createdAtUtc: string;
  updatedAtUtc?: string;
  isOwner: boolean;
  effectivePermission: string;
}

export interface HealthCheckResponse {
  status: string;
  environment: string;
  timestampUtc: string;
  version: string;
  checks: {
    database: {
      status: string;
      error?: string;
    };
  };
}

export interface AuditEventItem {
  id: string;
  userId?: string;
  userEmail?: string;
  action: string;
  entityType: string;
  entityId?: string;
  timestampUtc: string;
  ipAddress?: string;
  details?: string;
  status: string;
}

export interface DocumentPermissionItem {
  id: string;
  documentId: string;
  userId: string;
  userEmail: string;
  permissionLevel: string;
  grantedAtUtc: string;
}

export interface AuditVerificationResult {
  isValid: boolean;
  totalEventsVerified: number;
  violationDetails?: string;
  verifiedAtUtc: string;
}
