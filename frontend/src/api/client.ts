import type { AuthResponse, DocumentItem, HealthCheckResponse, User, AuditEventItem, DocumentPermissionItem, AuditVerificationResult } from '../types';

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || 'http://localhost:5115/api';

class ApiClient {
  private token: string | null = null;

  constructor() {
    this.token = localStorage.getItem('trustvault_token');
  }

  setToken(token: string | null) {
    this.token = token;
    if (token) {
      localStorage.setItem('trustvault_token', token);
    } else {
      localStorage.removeItem('trustvault_token');
    }
  }

  getToken(): string | null {
    return this.token;
  }

  private async request<T>(endpoint: string, options: RequestInit = {}): Promise<T> {
    const headers: Record<string, string> = {
      'Content-Type': 'application/json',
      ...(options.headers as Record<string, string>),
    };

    if (this.token) {
      headers['Authorization'] = `Bearer ${this.token}`;
    }

    const response = await fetch(`${API_BASE_URL}${endpoint}`, {
      ...options,
      headers,
    });

    if (!response.ok) {
      let errorMessage = `Request failed with status ${response.status}`;
      try {
        const errorData = await response.json();
        if (errorData.detail) {
          errorMessage = errorData.detail;
        } else if (errorData.message || errorData.Message) {
          errorMessage = errorData.message || errorData.Message;
        } else if (errorData.title) {
          errorMessage = errorData.title;
        }
      } catch {
        // Fallback to text or status
      }
      throw new Error(errorMessage);
    }

    if (response.status === 204) {
      return {} as T;
    }

    return response.json();
  }

  // Health Check
  async getHealth(): Promise<HealthCheckResponse> {
    return this.request<HealthCheckResponse>('/health');
  }

  // Authentication
  async login(email: string, password: string): Promise<AuthResponse> {
    const data = await this.request<AuthResponse>('/auth/login', {
      method: 'POST',
      body: JSON.stringify({ email, password }),
    });
    this.setToken(data.token);
    return data;
  }

  async register(email: string, password: string, fullName: string, role: string = 'Owner'): Promise<AuthResponse> {
    const data = await this.request<AuthResponse>('/auth/register', {
      method: 'POST',
      body: JSON.stringify({ email, password, fullName, role }),
    });
    this.setToken(data.token);
    return data;
  }

  async getMe(): Promise<User> {
    return this.request<User>('/auth/me');
  }

  logout() {
    this.setToken(null);
  }

  // Documents
  async getDocuments(): Promise<DocumentItem[]> {
    return this.request<DocumentItem[]>('/documents');
  }

  async searchDocuments(query: string): Promise<DocumentItem[]> {
    return this.request<DocumentItem[]>(`/documents/search?query=${encodeURIComponent(query)}`);
  }

  async createDocument(title: string, fileName: string, contentType: string, base64Content: string): Promise<DocumentItem> {
    return this.request<DocumentItem>('/documents', {
      method: 'POST',
      body: JSON.stringify({ title, fileName, contentType, base64Content }),
    });
  }

  async downloadDocument(id: string, fileName: string): Promise<void> {
    const headers: Record<string, string> = {};
    if (this.token) {
      headers['Authorization'] = `Bearer ${this.token}`;
    }

    const res = await fetch(`${API_BASE_URL}/documents/${id}/download`, { headers });
    if (!res.ok) {
      let errMsg = 'Failed to download document';
      try {
        const data = await res.json();
        errMsg = data.message || data.title || errMsg;
      } catch {
        if (res.status === 403) errMsg = 'Access Denied (403): You do not have permission to download this document.';
        if (res.status === 404) errMsg = 'Document Not Found (404)';
      }
      throw new Error(errMsg);
    }

    const blob = await res.blob();
    const url = window.URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = fileName;
    document.body.appendChild(a);
    a.click();
    window.URL.revokeObjectURL(url);
    document.body.removeChild(a);
  }

  async deleteDocument(id: string): Promise<void> {
    await this.request<void>(`/documents/${id}`, {
      method: 'DELETE',
    });
  }

  // Permissions Management
  async getPermissions(documentId: string): Promise<DocumentPermissionItem[]> {
    return this.request<DocumentPermissionItem[]>(`/documents/${documentId}/permissions`);
  }

  async grantPermission(documentId: string, userEmail: string, permissionLevel: 'Read' | 'Edit'): Promise<{ message: string }> {
    return this.request<{ message: string }>(`/documents/${documentId}/permissions`, {
      method: 'POST',
      body: JSON.stringify({ userEmail, permissionLevel }),
    });
  }

  async revokePermission(documentId: string, targetUserId: string): Promise<{ message: string }> {
    return this.request<{ message: string }>(`/documents/${documentId}/permissions/${targetUserId}`, {
      method: 'DELETE',
    });
  }

  // Audit Logs
  async getAuditLogs(): Promise<AuditEventItem[]> {
    return this.request<AuditEventItem[]>('/audit');
  }

  async getMyAuditLogs(): Promise<AuditEventItem[]> {
    return this.request<AuditEventItem[]>('/audit/me');
  }

  async verifyAuditIntegrity(): Promise<AuditVerificationResult> {
    return this.request<AuditVerificationResult>('/audit/verify');
  }
}

export const api = new ApiClient();
