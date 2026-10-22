import React, { useState, useEffect } from 'react';
import { api } from '../api/client';
import type { DocumentItem, DocumentPermissionItem } from '../types';

interface ManageAccessModalProps {
  document: DocumentItem;
  onClose: () => void;
  onPermissionsUpdated: () => void;
  onError: (msg: string) => void;
  onSuccess: (msg: string) => void;
}

export const ManageAccessModal: React.FC<ManageAccessModalProps> = ({
  document,
  onClose,
  onPermissionsUpdated,
  onError,
  onSuccess,
}) => {
  const [permissions, setPermissions] = useState<DocumentPermissionItem[]>([]);
  const [targetEmail, setTargetEmail] = useState('');
  const [level, setLevel] = useState<'Read' | 'Edit'>('Read');
  const [loading, setLoading] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [revokingId, setRevokingId] = useState<string | null>(null);

  const loadPermissions = React.useCallback(async () => {
    setLoading(true);
    try {
      const perms = await api.getPermissions(document.id);
      setPermissions(perms);
    } catch (err: any) {
      onError(`Failed to load document permissions: ${err.message}`);
    } finally {
      setLoading(false);
    }
  }, [document.id, onError]);

  useEffect(() => {
    loadPermissions();
  }, [loadPermissions]);

  const handleGrant = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!targetEmail.trim()) return;

    setSubmitting(true);
    try {
      const res = await api.grantPermission(document.id, targetEmail.trim(), level);
      onSuccess(res.message || `Granted ${level} access to ${targetEmail.trim()}`);
      setTargetEmail('');
      await loadPermissions();
      onPermissionsUpdated();
    } catch (err: any) {
      onError(`Failed to grant permission: ${err.message}`);
    } finally {
      setSubmitting(false);
    }
  };

  const handleRevoke = async (targetUserId: string, targetEmail: string) => {
    if (!window.confirm(`Revoke document access for ${targetEmail}? They will be immediately cut off.`)) {
      return;
    }

    setRevokingId(targetUserId);
    try {
      const res = await api.revokePermission(document.id, targetUserId);
      onSuccess(res.message || `Revoked access for ${targetEmail}`);
      await loadPermissions();
      onPermissionsUpdated();
    } catch (err: any) {
      onError(`Failed to revoke access: ${err.message}`);
    } finally {
      setRevokingId(null);
    }
  };

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal-dialog" onClick={(e) => e.stopPropagation()}>
        <div className="modal-header">
          <div>
            <h3 className="modal-title">Manage Access Permissions</h3>
            <div style={{ fontSize: '13px', color: 'var(--text-muted)', marginTop: '2px' }}>
              Document: <strong style={{ color: 'var(--text-primary)' }}>{document.title}</strong>
            </div>
          </div>
          <button className="modal-close-btn" onClick={onClose} aria-label="Close modal">
            ✕
          </button>
        </div>

        {/* Grant Permission Form */}
        <div style={{ marginBottom: '24px' }}>
          <h4 style={{ fontSize: '14px', fontWeight: 600, color: 'var(--text-primary)', marginBottom: '12px' }}>
            Grant Delegated Permission
          </h4>

          <form onSubmit={handleGrant}>
            <div style={{ display: 'flex', gap: '8px', alignItems: 'flex-start' }}>
              <div style={{ flex: 1 }}>
                <input
                  type="email"
                  placeholder="collaborator@enterprise.com"
                  value={targetEmail}
                  onChange={(e) => setTargetEmail(e.target.value)}
                  className="form-input"
                  required
                />
              </div>

              <div style={{ width: '120px' }}>
                <select
                  value={level}
                  onChange={(e) => setLevel(e.target.value as 'Read' | 'Edit')}
                  className="form-select"
                >
                  <option value="Read">Read Only</option>
                  <option value="Edit">Read & Edit</option>
                </select>
              </div>

              <button
                type="submit"
                className="btn btn-primary"
                disabled={submitting || !targetEmail.trim()}
              >
                {submitting ? 'Granting...' : 'Grant Access'}
              </button>
            </div>
            <div className="form-hint">
              Read allows downloading; Edit allows downloading and updating document metadata.
            </div>
          </form>
        </div>

        {/* Active Permissions List */}
        <div>
          <h4 style={{ fontSize: '14px', fontWeight: 600, color: 'var(--text-primary)', marginBottom: '10px' }}>
            Active Delegated Access List ({permissions.length})
          </h4>

          {loading ? (
            <div style={{ padding: '20px', textAlign: 'center', color: 'var(--text-muted)' }}>
              Loading permissions...
            </div>
          ) : permissions.length === 0 ? (
            <div
              style={{
                padding: '24px',
                textAlign: 'center',
                backgroundColor: 'var(--bg-input)',
                border: '1px dashed var(--border-subtle)',
                borderRadius: 'var(--radius-md)',
                color: 'var(--text-muted)',
                fontSize: '13px',
              }}
            >
              No external users currently have access. Only you (the document owner) can read or modify this file.
            </div>
          ) : (
            <div className="table-responsive-wrapper">
              <table className="tv-table">
                <thead>
                  <tr>
                    <th>User Email</th>
                    <th>Permission Level</th>
                    <th>Granted Date</th>
                    <th style={{ textAlign: 'right' }}>Action</th>
                  </tr>
                </thead>
                <tbody>
                  {permissions.map((p) => (
                    <tr key={p.id}>
                      <td style={{ fontWeight: 500, color: 'var(--text-primary)' }}>{p.userEmail}</td>
                      <td>
                        <span
                          className={`tv-badge ${
                            p.permissionLevel === 'Edit' ? 'tv-badge-editor' : 'tv-badge-viewer'
                          }`}
                        >
                          {p.permissionLevel}
                        </span>
                      </td>
                      <td style={{ fontSize: '12px', color: 'var(--text-muted)' }}>
                        {new Date(p.grantedAtUtc).toLocaleDateString()}
                      </td>
                      <td style={{ textAlign: 'right' }}>
                        <button
                          className="btn btn-ghost-danger btn-sm"
                          onClick={() => handleRevoke(p.userId, p.userEmail)}
                          disabled={revokingId === p.userId}
                        >
                          {revokingId === p.userId ? 'Revoking...' : 'Revoke'}
                        </button>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>

        <div className="modal-footer">
          <button className="btn btn-secondary" onClick={onClose}>
            Close
          </button>
        </div>
      </div>
    </div>
  );
};
