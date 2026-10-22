import React, { useState } from 'react';
import { api } from '../api/client';
import type { DocumentItem } from '../types';

interface EditTitleModalProps {
  document: DocumentItem;
  onClose: () => void;
  onTitleUpdated: (newTitle: string) => void;
  onError: (msg: string) => void;
  onSuccess: (msg: string) => void;
}

export const EditTitleModal: React.FC<EditTitleModalProps> = ({
  document,
  onClose,
  onTitleUpdated,
  onError,
  onSuccess,
}) => {
  const [title, setTitle] = useState(document.title);
  const [submitting, setSubmitting] = useState(false);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!title.trim()) return;

    setSubmitting(true);
    try {
      const res = await api.updateDocument(document.id, title.trim());
      onSuccess(res.message || 'Document title updated successfully.');
      onTitleUpdated(title.trim());
      onClose();
    } catch (err: any) {
      onError(`Failed to update title: ${err.message}`);
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <div className="modal-backdrop" onClick={onClose}>
      <div className="modal-dialog" style={{ maxWidth: '480px' }} onClick={(e) => e.stopPropagation()}>
        <div className="modal-header">
          <h3 className="modal-title">Edit Document Title</h3>
          <button className="modal-close-btn" onClick={onClose} aria-label="Close modal">
            ✕
          </button>
        </div>

        <form onSubmit={handleSubmit}>
          <div className="form-group">
            <label className="form-label" htmlFor="editTitleInput">
              Document Title
            </label>
            <input
              id="editTitleInput"
              type="text"
              value={title}
              onChange={(e) => setTitle(e.target.value)}
              className="form-input"
              required
              autoFocus
            />
            <div className="form-hint">
              Original file name: <code>{document.fileName}</code>
            </div>
          </div>

          <div className="modal-footer">
            <button type="button" className="btn btn-secondary" onClick={onClose}>
              Cancel
            </button>
            <button
              type="submit"
              className="btn btn-primary"
              disabled={submitting || !title.trim() || title.trim() === document.title}
            >
              {submitting ? 'Updating...' : 'Save Title'}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
};
