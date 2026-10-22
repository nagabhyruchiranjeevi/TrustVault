import React, { useState } from 'react';
import type { DocumentItem } from '../types';

interface DeleteConfirmModalProps {
  document: DocumentItem;
  onClose: () => void;
  onConfirmDelete: (doc: DocumentItem) => Promise<void>;
}

export const DeleteConfirmModal: React.FC<DeleteConfirmModalProps> = ({
  document,
  onClose,
  onConfirmDelete,
}) => {
  const [deleting, setDeleting] = useState(false);

  const handleConfirm = async () => {
    setDeleting(true);
    try {
      await onConfirmDelete(document);
      onClose();
    } finally {
      setDeleting(false);
    }
  };

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal-dialog" style={{ maxWidth: '460px' }} onClick={(e) => e.stopPropagation()}>
        <div className="modal-header">
          <h3 className="modal-title" style={{ color: 'var(--danger)' }}>
            Confirm Document Deletion
          </h3>
          <button className="modal-close-btn" onClick={onClose} aria-label="Close modal">
            ✕
          </button>
        </div>

        <div style={{ marginBottom: '20px' }}>
          <p style={{ fontSize: '14px', color: 'var(--text-primary)', marginBottom: '12px' }}>
            Are you sure you want to delete <strong style={{ color: '#fff' }}>"{document.title}"</strong>?
          </p>
          <div
            style={{
              padding: '12px 14px',
              backgroundColor: 'var(--danger-bg)',
              border: '1px solid var(--danger-border)',
              borderRadius: 'var(--radius-md)',
              fontSize: '12px',
              color: '#fca5a5',
              lineHeight: 1.5,
            }}
          >
            ⚠️ <strong>Zero-Trust Cutoff:</strong> This file will be immediately soft-deleted. All existing delegated Read and Edit permissions are terminated, and unauthorized download attempts will be rejected with 404/403.
          </div>
        </div>

        <div className="modal-footer">
          <button type="button" className="btn btn-secondary" onClick={onClose} disabled={deleting}>
            Cancel
          </button>
          <button
            type="button"
            className="btn btn-danger"
            onClick={handleConfirm}
            disabled={deleting}
          >
            {deleting ? 'Deleting...' : 'Delete Document'}
          </button>
        </div>
      </div>
    </div>
  );
};
