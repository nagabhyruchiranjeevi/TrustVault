import React, { useState, useRef } from 'react';
import { api } from '../api/client';

interface UploadViewProps {
  onUploadSuccess: (title: string) => void;
  onError: (msg: string) => void;
  onClearAlerts: () => void;
}

export const UploadView: React.FC<UploadViewProps> = ({
  onUploadSuccess,
  onError,
  onClearAlerts,
}) => {
  const [title, setTitle] = useState('');
  const [selectedFile, setSelectedFile] = useState<File | null>(null);
  const [isDragActive, setIsDragActive] = useState(false);
  const [uploading, setUploading] = useState(false);
  const [formError, setFormError] = useState<string | null>(null);
  const fileInputRef = useRef<HTMLInputElement>(null);

  const PERMITTED_EXTENSIONS = ['.pdf', '.png', '.jpg', '.jpeg', '.txt', '.docx', '.xlsx', '.csv', '.json', '.xml'];
  const MAX_FILE_SIZE_BYTES = 15 * 1024 * 1024; // 15 MB

  const validateFile = (file: File): boolean => {
    const ext = '.' + (file.name.split('.').pop()?.toLowerCase() || '');
    if (!PERMITTED_EXTENSIONS.includes(ext)) {
      setFormError(`Format "${ext}" is not permitted. Permitted types: PDF, PNG, JPG, TXT, DOCX, XLSX, CSV, JSON, XML.`);
      return false;
    }

    if (file.size > MAX_FILE_SIZE_BYTES) {
      setFormError(`File size (${(file.size / (1024 * 1024)).toFixed(1)} MB) exceeds the maximum limit of 15 MB.`);
      return false;
    }

    setFormError(null);
    return true;
  };

  const handleFileChange = (file: File) => {
    if (validateFile(file)) {
      setSelectedFile(file);
      if (!title.trim()) {
        // Derive clean title from filename
        const baseName = file.name.substring(0, file.name.lastIndexOf('.')) || file.name;
        setTitle(baseName.replace(/[_-]/g, ' '));
      }
    } else {
      setSelectedFile(null);
    }
  };

  const handleDragOver = (e: React.DragEvent) => {
    e.preventDefault();
    setIsDragActive(true);
  };

  const handleDragLeave = (e: React.DragEvent) => {
    e.preventDefault();
    setIsDragActive(false);
  };

  const handleDrop = (e: React.DragEvent) => {
    e.preventDefault();
    setIsDragActive(false);
    if (e.dataTransfer.files && e.dataTransfer.files[0]) {
      handleFileChange(e.dataTransfer.files[0]);
    }
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    onClearAlerts();

    if (!selectedFile) {
      setFormError('Please select a file to deposit.');
      return;
    }

    if (!title.trim()) {
      setFormError('Please enter a document title.');
      return;
    }

    setUploading(true);
    setFormError(null);

    try {
      const reader = new FileReader();
      reader.onload = async () => {
        try {
          const rawBase64 = (reader.result as string).split(',')[1];
          await api.createDocument(
            title.trim(),
            selectedFile.name,
            selectedFile.type || 'application/octet-stream',
            rawBase64
          );
          onUploadSuccess(`Document "${title.trim()}" successfully encrypted and vaulted.`);
          setTitle('');
          setSelectedFile(null);
          if (fileInputRef.current) {
            fileInputRef.current.value = '';
          }
        } catch (uploadErr: any) {
          onError(uploadErr.message || 'Failed to deposit document.');
        } finally {
          setUploading(false);
        }
      };
      reader.readAsDataURL(selectedFile);
    } catch {
      onError('An error occurred reading the binary file stream.');
      setUploading(false);
    }
  };

  const formatFileSize = (bytes: number): string => {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / (1024 * 1024)).toFixed(2)} MB`;
  };

  return (
    <div style={{ maxWidth: '720px', margin: '0 auto' }}>
      <div className="tv-card">
        <div className="tv-card-header">
          <div>
            <h2 className="tv-card-title">Deposit Document to Vault</h2>
            <p className="tv-card-description">
              Files are stored in private isolated vault storage decoupled from original filename.
            </p>
          </div>
        </div>

        <form onSubmit={handleSubmit}>
          {/* Drag and Drop Zone */}
          <div
            className={`upload-dropzone ${isDragActive ? 'drag-active' : ''}`}
            onDragOver={handleDragOver}
            onDragLeave={handleDragLeave}
            onDrop={handleDrop}
            onClick={() => fileInputRef.current?.click()}
          >
            <div className="upload-icon-circle">📥</div>
            <div className="upload-prompt-text">
              {selectedFile ? 'Change Selected File' : 'Drag & drop your document here'}
            </div>
            <div className="upload-subprompt-text">
              or click to browse from your device
            </div>

            <input
              ref={fileInputRef}
              type="file"
              style={{ display: 'none' }}
              onChange={(e) => {
                if (e.target.files && e.target.files[0]) {
                  handleFileChange(e.target.files[0]);
                }
              }}
            />
          </div>

          {/* Selected File Preview */}
          {selectedFile && (
            <div className="file-preview-card">
              <div className="file-preview-meta">
                <span className="file-type-badge">
                  {selectedFile.name.split('.').pop()?.toUpperCase() || 'FILE'}
                </span>
                <div>
                  <div style={{ fontSize: '13px', fontWeight: 600, color: 'var(--text-primary)' }}>
                    {selectedFile.name}
                  </div>
                  <div style={{ fontSize: '12px', color: 'var(--text-muted)' }}>
                    {formatFileSize(selectedFile.size)} • {selectedFile.type || 'Binary stream'}
                  </div>
                </div>
              </div>
              <button
                type="button"
                className="btn btn-secondary btn-sm"
                onClick={() => {
                  setSelectedFile(null);
                  if (fileInputRef.current) fileInputRef.current.value = '';
                }}
              >
                Remove
              </button>
            </div>
          )}

          {formError && (
            <div className="tv-alert tv-alert-error" style={{ marginTop: '16px' }}>
              <span>⚠️ {formError}</span>
            </div>
          )}

          {/* Title Field */}
          <div className="form-group" style={{ marginTop: '20px' }}>
            <label className="form-label" htmlFor="uploadTitleInput">
              Document Display Title
            </label>
            <input
              id="uploadTitleInput"
              type="text"
              placeholder="e.g. Q4 Executive Financial Summary"
              value={title}
              onChange={(e) => setTitle(e.target.value)}
              className="form-input"
              required
            />
            <div className="form-hint">
              This title is indexed for secure search queries across authorized collaborators.
            </div>
          </div>

          {/* Supported Types Info */}
          <div
            style={{
              padding: '12px 16px',
              backgroundColor: 'var(--bg-input)',
              border: '1px solid var(--border-subtle)',
              borderRadius: 'var(--radius-md)',
              marginBottom: '24px',
              fontSize: '12px',
              color: 'var(--text-secondary)',
            }}
          >
            <strong style={{ color: 'var(--text-primary)' }}>Permitted Formats:</strong> PDF, PNG, JPG, TXT, DOCX, XLSX, CSV, JSON, XML (Max 15 MB). Executable scripts and binaries are blocked by server-side policy.
          </div>

          <button
            type="submit"
            className="btn btn-primary btn-lg"
            style={{ width: '100%' }}
            disabled={uploading || !selectedFile}
          >
            {uploading ? (
              <span>Encrypting & Storing in Vault...</span>
            ) : (
              <span>Deposit & Secure File</span>
            )}
          </button>
        </form>
      </div>
    </div>
  );
};
