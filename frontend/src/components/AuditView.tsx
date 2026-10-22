import React, { useState } from 'react';
import type { User, AuditEventItem, AuditVerificationResult } from '../types';

interface AuditViewProps {
  currentUser: User;
  auditLogs: AuditEventItem[];
  auditVerification: AuditVerificationResult | null;
  onVerifyChain: () => void;
  onRefresh: () => void;
  loading: boolean;
}

export const AuditView: React.FC<AuditViewProps> = ({
  currentUser,
  auditLogs,
  auditVerification,
  onVerifyChain,
  onRefresh,
  loading,
}) => {
  const [filterAction, setFilterAction] = useState<string>('all');
  const [filterText, setFilterText] = useState<string>('');
  const isAdmin = currentUser.roles.includes('Admin');

  // Filter actions present in current log set
  const distinctActions = Array.from(new Set(auditLogs.map((l) => l.action))).filter(Boolean);

  const filteredLogs = auditLogs.filter((log) => {
    if (filterAction !== 'all' && log.action !== filterAction) return false;
    if (filterText.trim()) {
      const q = filterText.toLowerCase();
      const matchAction = log.action?.toLowerCase().includes(q);
      const matchDetails = log.details?.toLowerCase().includes(q);
      const matchEmail = log.userEmail?.toLowerCase().includes(q);
      const matchEntity = log.entityType?.toLowerCase().includes(q);
      if (!matchAction && !matchDetails && !matchEmail && !matchEntity) return false;
    }
    return true;
  });

  return (
    <div className="tv-card">
      <div className="tv-card-header">
        <div>
          <h2 className="tv-card-title">
            {isAdmin ? 'Tamper-Evident System Audit Trail' : 'My Security Activity Trail'}
          </h2>
          <p className="tv-card-description">
            {isAdmin
              ? 'Chronological SHA-256 chained audit records across all authentication, document, and permission events.'
              : 'Chronological record of operations performed by or affecting your account.'}
          </p>
        </div>

        <div style={{ display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' }}>
          {isAdmin && (
            <button
              className="btn btn-primary btn-sm"
              onClick={onVerifyChain}
              disabled={loading}
              title="Validate entire SHA-256 cryptographic chain from Genesis"
            >
              <span>🔒</span>
              <span>Verify Cryptographic Integrity</span>
            </button>
          )}

          <button
            className="btn btn-secondary btn-sm"
            onClick={onRefresh}
            disabled={loading}
          >
            <span>🔄</span>
            <span>Refresh</span>
          </button>
        </div>
      </div>

      {/* Verification Result Banner if verified */}
      {auditVerification && (
        <div
          className={`tv-alert ${auditVerification.isValid ? 'tv-alert-success' : 'tv-alert-error'}`}
          style={{ marginBottom: '20px' }}
        >
          <div>
            <strong>Cryptographic Chain Status: {auditVerification.isValid ? 'VERIFIED INTACT' : 'TAMPER DETECTED'}</strong>
            <div style={{ fontSize: '12px', marginTop: '3px' }}>
              Checked {auditVerification.totalEventsVerified} linked records from Genesis. {auditVerification.violationDetails || 'All SHA-256 block hashes and sequence numbers match.'}
            </div>
          </div>
        </div>
      )}

      {/* Filter and Search Bar */}
      <div style={{ display: 'flex', gap: '10px', marginBottom: '16px', flexWrap: 'wrap' }}>
        <select
          value={filterAction}
          onChange={(e) => setFilterAction(e.target.value)}
          className="form-select"
          style={{ width: '180px', padding: '7px 10px', fontSize: '13px' }}
        >
          <option value="all">All Actions ({auditLogs.length})</option>
          {distinctActions.map((act) => (
            <option key={act} value={act}>
              {act}
            </option>
          ))}
        </select>

        <input
          type="text"
          placeholder="Filter by keyword, actor, or detail..."
          value={filterText}
          onChange={(e) => setFilterText(e.target.value)}
          className="form-input"
          style={{ width: '280px', padding: '7px 12px', fontSize: '13px' }}
        />

        {(filterAction !== 'all' || filterText) && (
          <button
            className="btn btn-secondary btn-sm"
            onClick={() => {
              setFilterAction('all');
              setFilterText('');
            }}
          >
            Clear Filters
          </button>
        )}
      </div>

      {/* Audit Log Table */}
      {loading && filteredLogs.length === 0 ? (
        <div className="loading-skeleton">Loading audit trail...</div>
      ) : filteredLogs.length === 0 ? (
        <div className="empty-state-box">
          <div className="empty-state-icon">🛡️</div>
          <h3 className="empty-state-title">No Audit Events Recorded</h3>
          <p className="empty-state-desc">
            No audit records match the current filter criteria.
          </p>
        </div>
      ) : (
        <div className="table-responsive-wrapper">
          <table className="tv-table">
            <thead>
              <tr>
                <th>Seq #</th>
                <th>Timestamp (UTC)</th>
                <th>Action</th>
                <th>Entity</th>
                <th>Actor</th>
                <th>Status</th>
                <th>Operation Details</th>
                {isAdmin && <th>SHA-256 Fingerprint</th>}
              </tr>
            </thead>
            <tbody>
              {filteredLogs.map((log: any) => {
                const isSuccess = log.status === 'Success';
                const isForbidden = log.status === 'Forbidden';

                return (
                  <tr key={log.id}>
                    <td>
                      <span style={{ fontFamily: 'var(--font-mono)', fontWeight: 600, color: 'var(--text-muted)' }}>
                        #{log.sequenceNumber ?? '-'}
                      </span>
                    </td>
                    <td>
                      <span style={{ fontSize: '12px', color: 'var(--text-secondary)' }}>
                        {new Date(log.timestampUtc).toLocaleString()}
                      </span>
                    </td>
                    <td>
                      <span style={{ fontWeight: 600, color: 'var(--text-primary)' }}>
                        {log.action}
                      </span>
                    </td>
                    <td>
                      <span style={{ fontSize: '12px', color: 'var(--text-muted)' }}>
                        {log.entityType}
                      </span>
                    </td>
                    <td>
                      <span style={{ fontSize: '13px' }}>
                        {log.userEmail || 'System / Anonymous'}
                      </span>
                    </td>
                    <td>
                      <span
                        className={`tv-badge ${
                          isSuccess
                            ? 'tv-badge-success'
                            : isForbidden
                            ? 'tv-badge-forbidden'
                            : 'tv-badge-editor'
                        }`}
                      >
                        {log.status}
                      </span>
                    </td>
                    <td>
                      <div
                        style={{
                          fontSize: '12px',
                          color: 'var(--text-secondary)',
                          maxWidth: '320px',
                          wordBreak: 'break-word',
                        }}
                      >
                        {log.details || '—'}
                      </div>
                    </td>
                    {isAdmin && (
                      <td>
                        <span
                          style={{
                            fontFamily: 'var(--font-mono)',
                            fontSize: '11px',
                            color: 'var(--text-muted)',
                          }}
                          title={log.currentHash}
                        >
                          {log.currentHash
                            ? `${log.currentHash.substring(0, 8)}...${log.currentHash.substring(56)}`
                            : '—'}
                        </span>
                      </td>
                    )}
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}
    </div>
  );
};
