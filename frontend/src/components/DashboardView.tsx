import React from 'react';
import type { User, DocumentItem } from '../types';

interface DashboardViewProps {
  currentUser: User;
  documents: DocumentItem[];
  onNavigate: (view: 'dashboard' | 'my-documents' | 'shared' | 'upload' | 'audit') => void;
  onDownload: (doc: DocumentItem) => void;
  onManageAccess: (doc: DocumentItem) => void;
  loading: boolean;
}

export const DashboardView: React.FC<DashboardViewProps> = ({
  currentUser,
  documents,
  onNavigate,
  onDownload,
  onManageAccess,
  loading,
}) => {
  const ownedDocs = documents.filter((d) => d.isOwner);
  const sharedDocs = documents.filter((d) => !d.isOwner);
  const recentDocs = [...documents]
    .sort((a, b) => new Date(b.createdAtUtc).getTime() - new Date(a.createdAtUtc).getTime())
    .slice(0, 5);

  const formatFileSize = (bytes: number): string => {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
  };

  return (
    <div>
      {/* Welcome Banner */}
      <div style={{ marginBottom: '28px' }}>
        <h1 style={{ fontSize: '24px', fontWeight: 800, color: 'var(--text-primary)', letterSpacing: '-0.5px' }}>
          Welcome back, {currentUser.fullName || currentUser.email}
        </h1>
        <p style={{ fontSize: '14px', color: 'var(--text-secondary)', marginTop: '4px' }}>
          Enterprise Zero-Trust Workspace — Document storage is decoupled from filenames and access is validated per operation.
        </p>
      </div>

      {/* Metrics Grid based on real API data */}
      <div className="metrics-grid">
        <div className="metric-card">
          <div>
            <div className="metric-label">Accessible Documents</div>
            <div className="metric-value">{documents.length}</div>
            <div className="metric-subtext">Active vault records</div>
          </div>
          <div className="metric-icon-wrap">📑</div>
        </div>

        <div className="metric-card">
          <div>
            <div className="metric-label">My Vaulted Files</div>
            <div className="metric-value">{ownedDocs.length}</div>
            <div className="metric-subtext">Owned by your account</div>
          </div>
          <div className="metric-icon-wrap" style={{ color: '#10b981' }}>📁</div>
        </div>

        <div className="metric-card">
          <div>
            <div className="metric-label">Shared With Me</div>
            <div className="metric-value">{sharedDocs.length}</div>
            <div className="metric-subtext">Delegated access items</div>
          </div>
          <div className="metric-icon-wrap" style={{ color: '#0284c7' }}>👥</div>
        </div>

        <div className="metric-card">
          <div>
            <div className="metric-label">Audit & Integrity</div>
            <div className="metric-value" style={{ fontSize: '20px', color: '#10b981', paddingTop: '6px' }}>
              SHA-256
            </div>
            <div className="metric-subtext">Cryptographic hash chain</div>
          </div>
          <div className="metric-icon-wrap" style={{ color: '#8b5cf6' }}>🛡️</div>
        </div>
      </div>

      {/* Security Posture Highlights */}
      <div className="security-posture-box">
        <div className="security-posture-info">
          <div className="security-badge-icon">🔒</div>
          <div>
            <div className="security-posture-title">Zero-Trust Isolation Active</div>
            <div className="security-posture-desc">
              Administrators do not receive automatic read access to user documents. Every download, edit, and share request is cryptographically chained and authorized in real time.
            </div>
          </div>
        </div>
        <div style={{ display: 'flex', gap: '10px' }}>
          <button className="btn btn-secondary btn-sm" onClick={() => onNavigate('upload')}>
            Deposit File
          </button>
          <button className="btn btn-primary btn-sm" onClick={() => onNavigate('audit')}>
            Audit Trail
          </button>
        </div>
      </div>

      {/* Recent Vaulted Documents */}
      <div className="tv-card">
        <div className="tv-card-header">
          <div>
            <h2 className="tv-card-title">Recent Vault Documents</h2>
            <p className="tv-card-description">
              Most recently deposited or shared files in your secure workspace
            </p>
          </div>
          <div style={{ display: 'flex', gap: '8px' }}>
            <button className="btn btn-secondary btn-sm" onClick={() => onNavigate('my-documents')}>
              View All ({documents.length})
            </button>
          </div>
        </div>

        {loading && documents.length === 0 ? (
          <div className="loading-skeleton">Loading secure documents...</div>
        ) : recentDocs.length === 0 ? (
          <div className="empty-state-box">
            <div className="empty-state-icon">📂</div>
            <h3 className="empty-state-title">No Documents in Vault</h3>
            <p className="empty-state-desc">
              Your secure vault is currently empty. Upload your first document to store it in encrypted vault storage.
            </p>
            <button className="btn btn-primary" onClick={() => onNavigate('upload')}>
              Deposit First Document
            </button>
          </div>
        ) : (
          <div className="table-responsive-wrapper">
            <table className="tv-table">
              <thead>
                <tr>
                  <th>Title</th>
                  <th>File Name</th>
                  <th>Size</th>
                  <th>Owner</th>
                  <th>Permission</th>
                  <th>Deposited</th>
                  <th style={{ textAlign: 'right' }}>Actions</th>
                </tr>
              </thead>
              <tbody>
                {recentDocs.map((doc) => (
                  <tr key={doc.id}>
                    <td>
                      <span className="cell-doc-title">{doc.title}</span>
                    </td>
                    <td>
                      <span className="cell-filename">{doc.fileName}</span>
                    </td>
                    <td>{formatFileSize(doc.fileSizeBytes)}</td>
                    <td>{doc.ownerEmail || 'Unknown'}</td>
                    <td>
                      <span
                        className={`tv-badge ${
                          doc.isOwner
                            ? 'tv-badge-owner'
                            : doc.effectivePermission === 'Edit'
                            ? 'tv-badge-editor'
                            : 'tv-badge-viewer'
                        }`}
                      >
                        {doc.isOwner ? 'Owner' : doc.effectivePermission}
                      </span>
                    </td>
                    <td>{new Date(doc.createdAtUtc).toLocaleDateString()}</td>
                    <td style={{ textAlign: 'right' }}>
                      <div style={{ display: 'inline-flex', gap: '6px' }}>
                        <button
                          className="btn btn-secondary btn-sm"
                          onClick={() => onDownload(doc)}
                          title="Download document binary"
                        >
                          Download
                        </button>
                        {doc.isOwner && (
                          <button
                            className="btn btn-secondary btn-sm"
                            onClick={() => onManageAccess(doc)}
                            title="Manage access permissions"
                          >
                            Share
                          </button>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </div>
  );
};
