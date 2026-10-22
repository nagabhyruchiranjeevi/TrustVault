import React, { useState } from 'react';
import type { DocumentItem } from '../types';

interface DocumentsViewProps {
  viewMode: 'my-documents' | 'shared';
  documents: DocumentItem[];
  searchQuery: string;
  onSearchChange: (query: string) => void;
  onSearchSubmit: (e: React.FormEvent) => void;
  onClearSearch: () => void;
  onDownload: (doc: DocumentItem) => void;
  onEditTitle: (doc: DocumentItem) => void;
  onManageAccess: (doc: DocumentItem) => void;
  onDelete: (doc: DocumentItem) => void;
  onUploadClick: () => void;
  loading: boolean;
}

export const DocumentsView: React.FC<DocumentsViewProps> = ({
  viewMode,
  documents,
  searchQuery,
  onSearchChange,
  onSearchSubmit,
  onClearSearch,
  onDownload,
  onEditTitle,
  onManageAccess,
  onDelete,
  onUploadClick,
  loading,
}) => {
  const [filterType, setFilterType] = useState<string>('all');

  const filteredDocs = documents.filter((doc) => {
    // Mode filter
    if (viewMode === 'my-documents' && !doc.isOwner) return false;
    if (viewMode === 'shared' && doc.isOwner) return false;

    // File type category filter
    if (filterType !== 'all') {
      const ext = doc.fileName.split('.').pop()?.toLowerCase() || '';
      if (filterType === 'pdf' && ext !== 'pdf') return false;
      if (filterType === 'docs' && !['doc', 'docx', 'txt', 'rtf'].includes(ext)) return false;
      if (filterType === 'data' && !['csv', 'xlsx', 'json', 'xml'].includes(ext)) return false;
      if (filterType === 'images' && !['png', 'jpg', 'jpeg'].includes(ext)) return false;
    }

    return true;
  });

  const formatFileSize = (bytes: number): string => {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
  };

  const getFileCategoryIcon = (fileName: string): string => {
    const ext = fileName.split('.').pop()?.toLowerCase();
    switch (ext) {
      case 'pdf': return '📕';
      case 'docx':
      case 'doc':
      case 'txt': return '📄';
      case 'xlsx':
      case 'csv': return '📊';
      case 'json':
      case 'xml': return '⚙️';
      case 'png':
      case 'jpg':
      case 'jpeg': return '🖼️';
      default: return '📁';
    }
  };

  const isShared = viewMode === 'shared';

  return (
    <div className="tv-card">
      {/* View Header with Search & Filters */}
      <div className="tv-card-header">
        <div>
          <h2 className="tv-card-title">
            {isShared ? 'Documents Shared With Me' : 'My Vaulted Documents'}
          </h2>
          <p className="tv-card-description">
            {isShared
              ? 'Documents that other vault users have securely delegated to your account'
              : 'Secure files deposited under your ownership with isolated storage keys'}
          </p>
        </div>

        {/* Filter Controls */}
        <div style={{ display: 'flex', gap: '8px', alignItems: 'center', flexWrap: 'wrap' }}>
          {/* File Type Filter */}
          <select
            value={filterType}
            onChange={(e) => setFilterType(e.target.value)}
            className="form-select"
            style={{ width: '130px', padding: '7px 10px', fontSize: '13px' }}
          >
            <option value="all">All Formats</option>
            <option value="pdf">PDF Documents</option>
            <option value="docs">Text & Word</option>
            <option value="data">Data (CSV/XLSX)</option>
            <option value="images">Images</option>
          </select>

          {/* Search Bar */}
          <form onSubmit={onSearchSubmit} style={{ display: 'flex', gap: '6px' }}>
            <input
              type="text"
              placeholder="Search title or filename..."
              value={searchQuery}
              onChange={(e) => onSearchChange(e.target.value)}
              className="form-input"
              style={{ width: '210px', padding: '7px 10px', fontSize: '13px' }}
            />
            {searchQuery && (
              <button
                type="button"
                className="btn btn-secondary btn-sm"
                onClick={onClearSearch}
                title="Clear filter"
              >
                ✕
              </button>
            )}
            <button type="submit" className="btn btn-secondary btn-sm" disabled={loading}>
              Filter
            </button>
          </form>

          {!isShared && (
            <button className="btn btn-primary btn-sm" onClick={onUploadClick}>
              <span>➕</span>
              <span>Deposit</span>
            </button>
          )}
        </div>
      </div>

      {/* Documents Table */}
      {loading && filteredDocs.length === 0 ? (
        <div className="loading-skeleton">Loading documents...</div>
      ) : filteredDocs.length === 0 ? (
        <div className="empty-state-box">
          <div className="empty-state-icon">{isShared ? '🤝' : '📂'}</div>
          <h3 className="empty-state-title">
            {searchQuery || filterType !== 'all'
              ? 'No Documents Found'
              : isShared
              ? 'No Shared Documents'
              : 'Your Vault is Empty'}
          </h3>
          <p className="empty-state-desc">
            {searchQuery || filterType !== 'all'
              ? `No documents matched the query "${searchQuery}" or selected format filter.`
              : isShared
              ? 'No other users have granted your account Read or Edit permissions on their files yet.'
              : 'Deposit sensitive business files into TrustVault to benefit from zero-trust access control.'}
          </p>
          {!isShared && (
            <button className="btn btn-primary" onClick={onUploadClick}>
              Deposit First Document
            </button>
          )}
        </div>
      ) : (
        <div className="table-responsive-wrapper">
          <table className="tv-table">
            <thead>
              <tr>
                <th>Document</th>
                <th>File Name</th>
                <th>Size</th>
                <th>{isShared ? 'Owner' : 'Access Level'}</th>
                <th>Deposited</th>
                <th style={{ textAlign: 'right' }}>Actions</th>
              </tr>
            </thead>
            <tbody>
              {filteredDocs.map((doc) => {
                const canEdit = doc.isOwner || doc.effectivePermission === 'Edit';
                return (
                  <tr key={doc.id}>
                    <td>
                      <div style={{ display: 'flex', alignItems: 'center', gap: '10px' }}>
                        <span style={{ fontSize: '18px' }}>{getFileCategoryIcon(doc.fileName)}</span>
                        <div>
                          <div className="cell-doc-title">{doc.title}</div>
                          {doc.updatedAtUtc && (
                            <div style={{ fontSize: '11px', color: 'var(--text-muted)' }}>
                              Updated {new Date(doc.updatedAtUtc).toLocaleDateString()}
                            </div>
                          )}
                        </div>
                      </div>
                    </td>
                    <td>
                      <span className="cell-filename">{doc.fileName}</span>
                    </td>
                    <td>{formatFileSize(doc.fileSizeBytes)}</td>
                    <td>
                      {isShared ? (
                        <div>
                          <div style={{ fontSize: '13px', fontWeight: 500 }}>{doc.ownerEmail || 'Unknown'}</div>
                          <span
                            className={`tv-badge ${
                              doc.effectivePermission === 'Edit' ? 'tv-badge-editor' : 'tv-badge-viewer'
                            }`}
                            style={{ marginTop: '2px' }}
                          >
                            {doc.effectivePermission} Access
                          </span>
                        </div>
                      ) : (
                        <span className="tv-badge tv-badge-owner">Owner</span>
                      )}
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

                        {/* Edit Title button enabled for Owner or Editor */}
                        {canEdit && (
                          <button
                            className="btn btn-secondary btn-sm"
                            onClick={() => onEditTitle(doc)}
                            title="Edit document title"
                          >
                            Edit
                          </button>
                        )}

                        {/* Owner only actions: Share and Delete */}
                        {doc.isOwner && (
                          <>
                            <button
                              className="btn btn-secondary btn-sm"
                              onClick={() => onManageAccess(doc)}
                              title="Manage sharing & permissions"
                            >
                              Share
                            </button>
                            <button
                              className="btn btn-ghost-danger btn-sm"
                              onClick={() => onDelete(doc)}
                              title="Delete document"
                            >
                              Delete
                            </button>
                          </>
                        )}
                      </div>
                    </td>
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
