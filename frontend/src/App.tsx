import React, { useState, useEffect } from 'react';
import { api } from './api/client';
import type { User, DocumentItem, HealthCheckResponse, AuditEventItem, DocumentPermissionItem, AuditVerificationResult } from './types';

export const App: React.FC = () => {
  const [currentUser, setCurrentUser] = useState<User | null>(null);
  const [health, setHealth] = useState<HealthCheckResponse | null>(null);
  const [activeTab, setActiveTab] = useState<'documents' | 'upload' | 'audit'>('documents');
  const [authMode, setAuthMode] = useState<'login' | 'register'>('login');

  // Form states
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [fullName, setFullName] = useState('');
  const [role, setRole] = useState('Owner');

  // Document states
  const [documents, setDocuments] = useState<DocumentItem[]>([]);
  const [searchQuery, setSearchQuery] = useState('');
  const [auditLogs, setAuditLogs] = useState<AuditEventItem[]>([]);
  const [auditVerification, setAuditVerification] = useState<AuditVerificationResult | null>(null);
  const [uploadTitle, setUploadTitle] = useState('');
  const [selectedFile, setSelectedFile] = useState<File | null>(null);

  // Permission Modal states
  const [permissionModalDoc, setPermissionModalDoc] = useState<DocumentItem | null>(null);
  const [activePermissions, setActivePermissions] = useState<DocumentPermissionItem[]>([]);
  const [grantTargetEmail, setGrantTargetEmail] = useState('');
  const [grantPermissionLevel, setGrantPermissionLevel] = useState<'Read' | 'Edit'>('Read');
  const [permissionLoading, setPermissionLoading] = useState(false);

  // Status states
  const [loading, setLoading] = useState<boolean>(false);
  const [error, setError] = useState<string | null>(null);
  const [successMsg, setSuccessMsg] = useState<string | null>(null);

  // Initial load: check health and existing token
  useEffect(() => {
    checkHealth();
    if (api.getToken()) {
      fetchUserProfile();
    }
  }, []);

  // Fetch data when activeTab or user changes
  useEffect(() => {
    if (currentUser) {
      if (activeTab === 'documents') {
        fetchDocuments();
      } else if (activeTab === 'audit') {
        fetchAuditLogs();
      }
    }
  }, [currentUser, activeTab]);

  const checkHealth = async () => {
    try {
      const res = await api.getHealth();
      setHealth(res);
    } catch {
      setHealth(null);
    }
  };

  const fetchUserProfile = async () => {
    setLoading(true);
    try {
      const user = await api.getMe();
      setCurrentUser(user);
    } catch {
      api.logout();
      setCurrentUser(null);
    } finally {
      setLoading(false);
    }
  };

  const fetchDocuments = async () => {
    setLoading(true);
    setError(null);
    try {
      const docs = searchQuery.trim()
        ? await api.searchDocuments(searchQuery.trim())
        : await api.getDocuments();
      setDocuments(docs);
    } catch (err: any) {
      setError(err.message || 'Failed to load documents');
    } finally {
      setLoading(false);
    }
  };

  const handleSearchSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    fetchDocuments();
  };

  const handleClearSearch = async () => {
    setSearchQuery('');
    setLoading(true);
    try {
      const docs = await api.getDocuments();
      setDocuments(docs);
    } catch (err: any) {
      setError(err.message || 'Failed to load documents');
    } finally {
      setLoading(false);
    }
  };

  const fetchAuditLogs = async () => {
    setLoading(true);
    setError(null);
    try {
      if (currentUser?.roles.includes('Admin')) {
        const logs = await api.getAuditLogs();
        setAuditLogs(logs);
      } else {
        const logs = await api.getMyAuditLogs();
        setAuditLogs(logs);
      }
    } catch (err: any) {
      setError(err.message || 'Failed to load audit logs');
    } finally {
      setLoading(false);
    }
  };

  const handleVerifyAuditChain = async () => {
    setLoading(true);
    setError(null);
    try {
      const res = await api.verifyAuditIntegrity();
      setAuditVerification(res);
      setSuccessMsg(`Hash chain integrity verified! Checked ${res.totalEventsVerified} events from Genesis.`);
    } catch (err: any) {
      setError(`Audit Integrity Check Failed: ${err.message}`);
    } finally {
      setLoading(false);
    }
  };

  const handleAuthSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setSuccessMsg(null);
    setLoading(true);

    try {
      if (authMode === 'login') {
        const res = await api.login(email, password);
        setCurrentUser(res.user);
        setSuccessMsg(`Authenticated as ${res.user.email} (${res.user.roles.join(', ')})`);
      } else {
        const res = await api.register(email, password, fullName, role);
        setCurrentUser(res.user);
        setSuccessMsg(`Account created and signed in as ${res.user.email}`);
      }
    } catch (err: any) {
      setError(err.message || 'Authentication failed');
    } finally {
      setLoading(false);
    }
  };

  const handleLogout = () => {
    api.logout();
    setCurrentUser(null);
    setDocuments([]);
    setAuditLogs([]);
    setAuditVerification(null);
    setPermissionModalDoc(null);
    setSuccessMsg(null);
    setError(null);
  };

  const handleFileUpload = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!selectedFile || !uploadTitle.trim()) {
      setError('Please provide a document title and select a file.');
      return;
    }

    setLoading(true);
    setError(null);
    setSuccessMsg(null);

    try {
      const reader = new FileReader();
      reader.onload = async () => {
        const base64Content = (reader.result as string).split(',')[1];
        try {
          await api.createDocument(
            uploadTitle,
            selectedFile.name,
            selectedFile.type || 'application/octet-stream',
            base64Content
          );
          setSuccessMsg(`Document "${uploadTitle}" safely vaulted with encrypted storage key!`);
          setUploadTitle('');
          setSelectedFile(null);
          setActiveTab('documents');
          fetchDocuments();
        } catch (uploadErr: any) {
          setError(uploadErr.message || 'Failed to save document');
        } finally {
          setLoading(false);
        }
      };
      reader.readAsDataURL(selectedFile);
    } catch {
      setError('Error reading file data');
      setLoading(false);
    }
  };

  const handleDownload = async (doc: DocumentItem) => {
    setError(null);
    try {
      await api.downloadDocument(doc.id, doc.fileName);
      setSuccessMsg(`Downloaded '${doc.fileName}' successfully.`);
    } catch (err: any) {
      setError(`Download Denied: ${err.message}`);
    }
  };

  const handleDelete = async (doc: DocumentItem) => {
    if (!window.confirm(`Are you sure you want to delete "${doc.title}"? It will be soft-deleted immediately.`)) {
      return;
    }
    setError(null);
    try {
      await api.deleteDocument(doc.id);
      setSuccessMsg(`Document "${doc.title}" was soft-deleted. Unauthorized downloads are now prevented.`);
      fetchDocuments();
    } catch (err: any) {
      setError(`Failed to delete document: ${err.message}`);
    }
  };

  // Permission Management Modal Handlers
  const openPermissionModal = async (doc: DocumentItem) => {
    setPermissionModalDoc(doc);
    setGrantTargetEmail('');
    setGrantPermissionLevel('Read');
    setPermissionLoading(true);
    setError(null);
    try {
      const perms = await api.getPermissions(doc.id);
      setActivePermissions(perms);
    } catch (err: any) {
      setError(`Failed to load permissions: ${err.message}`);
    } finally {
      setPermissionLoading(false);
    }
  };

  const handleGrantPermissionSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!permissionModalDoc || !grantTargetEmail.trim()) return;

    setPermissionLoading(true);
    setError(null);
    try {
      const res = await api.grantPermission(permissionModalDoc.id, grantTargetEmail.trim(), grantPermissionLevel);
      setSuccessMsg(res.message);
      setGrantTargetEmail('');
      const updatedPerms = await api.getPermissions(permissionModalDoc.id);
      setActivePermissions(updatedPerms);
    } catch (err: any) {
      setError(`Failed to grant permission: ${err.message}`);
    } finally {
      setPermissionLoading(false);
    }
  };

  const handleRevokePermission = async (targetUserId: string, targetEmail: string) => {
    if (!permissionModalDoc) return;
    setPermissionLoading(true);
    setError(null);
    try {
      const res = await api.revokePermission(permissionModalDoc.id, targetUserId);
      setSuccessMsg(res.message || `Revoked access for ${targetEmail}`);
      const updatedPerms = await api.getPermissions(permissionModalDoc.id);
      setActivePermissions(updatedPerms);
    } catch (err: any) {
      setError(`Failed to revoke permission: ${err.message}`);
    } finally {
      setPermissionLoading(false);
    }
  };

  return (
    <div className="app-container">
      {/* Top Header */}
      <header className="app-header glass-panel">
        <div className="logo-badge">
          <div className="logo-icon">TV</div>
          <div>
            <div style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
              <span className="logo-title">TrustVault</span>
              <span className="logo-tag">DAY 3 FINAL</span>
            </div>
            <div style={{ fontSize: '12px', color: 'var(--text-muted)' }}>
              Zero-Trust Secure Document Vault & Audit Chain
            </div>
          </div>
        </div>

        <div style={{ display: 'flex', alignItems: 'center', gap: '16px' }}>
          {/* Health indicator */}
          <div className={`badge ${health?.status === 'Healthy' ? 'badge-healthy' : 'badge-degraded'}`}>
            Backend: {health ? health.status : 'Connecting...'} ({health?.environment || 'Dev'})
          </div>

          {currentUser && (
            <div style={{ display: 'flex', alignItems: 'center', gap: '12px' }}>
              <div style={{ textAlign: 'right' }}>
                <div style={{ fontSize: '14px', fontWeight: 600 }}>{currentUser.fullName}</div>
                <div style={{ fontSize: '12px', color: 'var(--text-muted)' }}>{currentUser.email}</div>
              </div>
              <span className={`badge ${currentUser.roles.includes('Admin') ? 'badge-admin' : 'badge-owner'}`}>
                {currentUser.roles.join(', ') || 'User'}
              </span>
              <button className="btn btn-secondary" onClick={handleLogout} style={{ padding: '6px 12px' }}>
                Sign Out
              </button>
            </div>
          )}
        </div>
      </header>

      {/* Alerts */}
      {error && <div className="alert alert-error">{error}</div>}
      {successMsg && <div className="alert alert-success">{successMsg}</div>}

      {/* Main Content Area */}
      {!currentUser ? (
        /* Auth Screen */
        <div style={{ maxWidth: '440px', margin: '40px auto' }}>
          <div className="glass-panel" style={{ padding: '32px' }}>
            <div style={{ display: 'flex', gap: '8px', marginBottom: '24px' }}>
              <button
                className={`tab-btn ${authMode === 'login' ? 'active' : ''}`}
                style={{ flex: 1 }}
                onClick={() => { setAuthMode('login'); setError(null); }}
              >
                Sign In
              </button>
              <button
                className={`tab-btn ${authMode === 'register' ? 'active' : ''}`}
                style={{ flex: 1 }}
                onClick={() => { setAuthMode('register'); setError(null); }}
              >
                Register
              </button>
            </div>

            <form onSubmit={handleAuthSubmit}>
              {authMode === 'register' && (
                <>
                  <div className="form-group">
                    <label>Full Name</label>
                    <input
                      type="text"
                      placeholder="Jane Doe"
                      value={fullName}
                      onChange={(e) => setFullName(e.target.value)}
                      required
                    />
                  </div>
                  <div className="form-group">
                    <label>Assigned Role</label>
                    <select value={role} onChange={(e) => setRole(e.target.value)}>
                      <option value="Owner">Document Owner (Can upload and share)</option>
                      <option value="Viewer">Viewer (Read-only on permitted items)</option>
                    </select>
                  </div>
                </>
              )}

              <div className="form-group">
                <label>Email Address</label>
                <input
                  type="email"
                  placeholder="user@example.com"
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                  required
                />
              </div>

              <div className="form-group">
                <label>Password</label>
                <input
                  type="password"
                  placeholder="Min 8 chars, 1 Upper, 1 Lower, 1 Num, 1 Spec"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  required
                />
              </div>

              <button
                type="submit"
                className="btn btn-primary"
                style={{ width: '100%', marginTop: '8px' }}
                disabled={loading}
              >
                {loading ? 'Processing...' : authMode === 'login' ? 'Sign In to Vault' : 'Create Vault Account'}
              </button>
            </form>

            <div style={{ marginTop: '20px', fontSize: '12px', color: 'var(--text-muted)', textAlign: 'center' }}>
              Default Admin: <code>admin@trustvault.local</code> / <code>Admin#TrustVault2026!</code>
            </div>
          </div>
        </div>
      ) : (
        /* Authenticated Dashboard */
        <div>
          {/* Security Workflow Checklist Banner */}
          <div className="workflow-card">
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
              <span style={{ fontWeight: 600, fontSize: '13px', color: '#60a5fa' }}>
                🛡️ Security Verification Flow:
              </span>
              <span style={{ fontSize: '12px', color: 'var(--text-muted)' }}>
                1. Upload (Owner) ➜ 2. Grant Read (Viewer) ➜ 3. Download (Viewer) ➜ 4. Revoke Access ➜ 5. Verify Denied (403) ➜ 6. Audit Trail
              </span>
            </div>
          </div>

          {/* Navigation Tabs */}
          <div className="nav-tabs">
            <button
              className={`tab-btn ${activeTab === 'documents' ? 'active' : ''}`}
              onClick={() => setActiveTab('documents')}
            >
              Vault Documents ({documents.length})
            </button>
            <button
              className={`tab-btn ${activeTab === 'upload' ? 'active' : ''}`}
              onClick={() => setActiveTab('upload')}
            >
              Upload Document
            </button>
            <button
              className={`tab-btn ${activeTab === 'audit' ? 'active' : ''}`}
              onClick={() => setActiveTab('audit')}
            >
              {currentUser.roles.includes('Admin') ? 'System Audit Trail' : 'My Activity Log'}
            </button>
          </div>

          {/* Tab 1: Documents */}
          {activeTab === 'documents' && (
            <div className="glass-panel" style={{ padding: '24px' }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '16px', flexWrap: 'wrap', gap: '12px' }}>
                <h3 style={{ fontSize: '18px', fontWeight: 600 }}>Your Secure Documents</h3>

                {/* Search Bar */}
                <form onSubmit={handleSearchSubmit} className="search-input-group">
                  <input
                    type="text"
                    placeholder="Search titles/files..."
                    value={searchQuery}
                    onChange={(e) => setSearchQuery(e.target.value)}
                    style={{ width: '220px', padding: '6px 12px', fontSize: '13px' }}
                  />
                  <button type="submit" className="btn btn-secondary" style={{ padding: '6px 12px', fontSize: '13px' }}>
                    Search
                  </button>
                  {searchQuery && (
                    <button type="button" className="btn btn-secondary" onClick={handleClearSearch} style={{ padding: '6px 10px', fontSize: '13px' }}>
                      Clear
                    </button>
                  )}
                  <button type="button" className="btn btn-secondary" onClick={fetchDocuments} disabled={loading} style={{ padding: '6px 12px', fontSize: '13px' }}>
                    Refresh
                  </button>
                </form>
              </div>

              {loading && documents.length === 0 ? (
                <div style={{ padding: '40px', textAlign: 'center', color: 'var(--text-muted)' }}>
                  Loading secure documents...
                </div>
              ) : documents.length === 0 ? (
                <div style={{ padding: '40px', textAlign: 'center', color: 'var(--text-muted)' }}>
                  {searchQuery ? `No documents matched "${searchQuery}". (Zero-leakage guarantee active).` : 'No documents found in your vault. Deposit a file to get started.'}
                </div>
              ) : (
                <div className="table-container">
                  <table>
                    <thead>
                      <tr>
                        <th>Title</th>
                        <th>File Name</th>
                        <th>Size</th>
                        <th>Owner</th>
                        <th>Your Permission</th>
                        <th>Uploaded</th>
                        <th>Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      {documents.map((doc) => (
                        <tr key={doc.id}>
                          <td style={{ fontWeight: 600 }}>{doc.title}</td>
                          <td><code>{doc.fileName}</code></td>
                          <td>{(doc.fileSizeBytes / 1024).toFixed(1)} KB</td>
                          <td>{doc.ownerEmail || 'Unknown'}</td>
                          <td>
                            <span className={`badge ${doc.isOwner ? 'badge-owner' : 'badge-viewer'}`}>
                              {doc.effectivePermission}
                            </span>
                          </td>
                          <td style={{ color: 'var(--text-muted)', fontSize: '12px' }}>
                            {new Date(doc.createdAtUtc).toLocaleDateString()}
                          </td>
                          <td>
                            <div style={{ display: 'flex', gap: '6px' }}>
                              <button
                                className="btn btn-secondary"
                                style={{ padding: '4px 10px', fontSize: '12px' }}
                                onClick={() => handleDownload(doc)}
                              >
                                Download
                              </button>

                              {doc.isOwner && (
                                <>
                                  <button
                                    className="btn btn-primary"
                                    style={{ padding: '4px 10px', fontSize: '12px' }}
                                    onClick={() => openPermissionModal(doc)}
                                  >
                                    Manage Access
                                  </button>
                                  <button
                                    className="btn btn-secondary"
                                    style={{ padding: '4px 8px', fontSize: '12px', color: '#f87171' }}
                                    onClick={() => handleDelete(doc)}
                                  >
                                    Delete
                                  </button>
                                </>
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
          )}

          {/* Tab 2: Upload */}
          {activeTab === 'upload' && (
            <div className="glass-panel" style={{ maxWidth: '600px', margin: '0 auto', padding: '32px' }}>
              <h3 style={{ fontSize: '18px', fontWeight: 600, marginBottom: '20px' }}>
                Deposit Document into Vault
              </h3>

              <form onSubmit={handleFileUpload}>
                <div className="form-group">
                  <label>Document Title</label>
                  <input
                    type="text"
                    placeholder="e.g. Q4 Financial Projections"
                    value={uploadTitle}
                    onChange={(e) => setUploadTitle(e.target.value)}
                    required
                  />
                </div>

                <div className="form-group">
                  <label>Select File</label>
                  <input
                    type="file"
                    onChange={(e) => {
                      if (e.target.files && e.target.files[0]) {
                        setSelectedFile(e.target.files[0]);
                      }
                    }}
                    required
                  />
                  <div style={{ fontSize: '12px', color: 'var(--text-muted)', marginTop: '6px' }}>
                    Files are stored in private isolated vault storage decoupled from original filename.
                  </div>
                </div>

                <button
                  type="submit"
                  className="btn btn-primary"
                  style={{ width: '100%', marginTop: '16px' }}
                  disabled={loading}
                >
                  {loading ? 'Vaulting Document...' : 'Secure & Deposit'}
                </button>
              </form>
            </div>
          )}

          {/* Tab 3: Audit Trail */}
          {activeTab === 'audit' && (
            <div className="glass-panel" style={{ padding: '24px' }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '16px', flexWrap: 'wrap', gap: '12px' }}>
                <div>
                  <h3 style={{ fontSize: '18px', fontWeight: 600 }}>
                    {currentUser.roles.includes('Admin') ? 'Tamper-Evident Security Audit Trail' : 'My Audit Events'}
                  </h3>
                  <p style={{ fontSize: '13px', color: 'var(--text-muted)' }}>
                    {currentUser.roles.includes('Admin')
                      ? 'Admins can verify hash chain integrity, but cannot view document contents without explicit permission.'
                      : 'Immutable chronological record of operations on your account and documents.'}
                  </p>
                </div>
                <div style={{ display: 'flex', gap: '8px' }}>
                  {currentUser.roles.includes('Admin') && (
                    <button
                      className="btn btn-primary"
                      onClick={handleVerifyAuditChain}
                      disabled={loading}
                      style={{ fontSize: '13px' }}
                    >
                      Verify Hash Chain Integrity
                    </button>
                  )}
                  <button className="btn btn-secondary" onClick={fetchAuditLogs} disabled={loading} style={{ fontSize: '13px' }}>
                    Refresh Logs
                  </button>
                </div>
              </div>

              {auditVerification && (
                <div className={`alert ${auditVerification.isValid ? 'alert-success' : 'alert-error'}`} style={{ marginBottom: '16px' }}>
                  <strong>Hash Chain Status:</strong> {auditVerification.isValid ? 'VALID' : 'TAMPERED'} — Verified {auditVerification.totalEventsVerified} events. {auditVerification.violationDetails || 'All deterministic SHA-256 links intact.'}
                </div>
              )}

              <div className="table-container">
                <table>
                  <thead>
                    <tr>
                      <th>Seq #</th>
                      <th>Timestamp (UTC)</th>
                      <th>Action</th>
                      <th>Entity</th>
                      <th>User</th>
                      <th>Status</th>
                      <th>Details</th>
                      {currentUser.roles.includes('Admin') && <th>Hash Fingerprint</th>}
                    </tr>
                  </thead>
                  <tbody>
                    {auditLogs.map((log: any) => (
                      <tr key={log.id}>
                        <td style={{ fontWeight: 600, color: 'var(--text-muted)' }}>#{log.sequenceNumber || '-'}</td>
                        <td style={{ fontSize: '12px', color: 'var(--text-muted)' }}>
                          {new Date(log.timestampUtc).toLocaleString()}
                        </td>
                        <td style={{ fontWeight: 600 }}>{log.action}</td>
                        <td>{log.entityType}</td>
                        <td>{log.userEmail || 'System/Anonymous'}</td>
                        <td>
                          <span
                            className={`badge ${
                              log.status === 'Success'
                                ? 'badge-healthy'
                                : log.status === 'Forbidden'
                                ? 'badge-admin'
                                : 'badge-degraded'
                            }`}
                          >
                            {log.status}
                          </span>
                        </td>
                        <td style={{ fontSize: '13px', maxWidth: '300px', wordBreak: 'break-word' }}>
                          {log.details || '-'}
                        </td>
                        {currentUser.roles.includes('Admin') && (
                          <td style={{ fontSize: '11px', fontFamily: 'monospace', color: 'var(--text-muted)' }}>
                            {log.currentHash ? `${log.currentHash.substring(0, 8)}...${log.currentHash.substring(56)}` : '-'}
                          </td>
                        )}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </div>
      )}

      {/* Permission Management Modal */}
      {permissionModalDoc && (
        <div className="modal-overlay" onClick={() => setPermissionModalDoc(null)}>
          <div className="modal-content" onClick={(e) => e.stopPropagation()}>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '16px' }}>
              <h3 style={{ fontSize: '18px', fontWeight: 600 }}>Manage Access: {permissionModalDoc.title}</h3>
              <button
                className="btn btn-secondary"
                style={{ padding: '4px 8px', fontSize: '14px' }}
                onClick={() => setPermissionModalDoc(null)}
              >
                ✕
              </button>
            </div>

            {/* Grant Permission Form */}
            <form onSubmit={handleGrantPermissionSubmit} style={{ marginBottom: '24px' }}>
              <div style={{ display: 'flex', gap: '8px', alignItems: 'flex-end' }}>
                <div style={{ flex: 1 }}>
                  <label>Grant to User Email</label>
                  <input
                    type="email"
                    placeholder="colleague@trustvault.local"
                    value={grantTargetEmail}
                    onChange={(e) => setGrantTargetEmail(e.target.value)}
                    required
                  />
                </div>
                <div style={{ width: '110px' }}>
                  <label>Level</label>
                  <select
                    value={grantPermissionLevel}
                    onChange={(e) => setGrantPermissionLevel(e.target.value as 'Read' | 'Edit')}
                  >
                    <option value="Read">Read</option>
                    <option value="Edit">Edit</option>
                  </select>
                </div>
                <button
                  type="submit"
                  className="btn btn-primary"
                  disabled={permissionLoading}
                  style={{ height: '42px', padding: '0 16px' }}
                >
                  Grant
                </button>
              </div>
            </form>

            {/* Current Active Permissions List */}
            <h4 style={{ fontSize: '14px', fontWeight: 600, marginBottom: '10px' }}>Current Active Permissions</h4>
            {permissionLoading && activePermissions.length === 0 ? (
              <div style={{ color: 'var(--text-muted)', fontSize: '13px' }}>Loading permissions...</div>
            ) : activePermissions.length === 0 ? (
              <div style={{ color: 'var(--text-muted)', fontSize: '13px', fontStyle: 'italic' }}>
                No external permissions granted. Only you (the owner) can access this document.
              </div>
            ) : (
              <div className="table-container">
                <table>
                  <thead>
                    <tr>
                      <th>User</th>
                      <th>Permission</th>
                      <th>Granted</th>
                      <th>Action</th>
                    </tr>
                  </thead>
                  <tbody>
                    {activePermissions.map((perm) => (
                      <tr key={perm.id}>
                        <td style={{ fontWeight: 500 }}>{perm.userEmail}</td>
                        <td>
                          <span className="badge badge-viewer">{perm.permissionLevel}</span>
                        </td>
                        <td style={{ fontSize: '12px', color: 'var(--text-muted)' }}>
                          {new Date(perm.grantedAtUtc).toLocaleDateString()}
                        </td>
                        <td>
                          <button
                            className="btn btn-secondary"
                            style={{ padding: '4px 8px', fontSize: '11px', color: '#f87171' }}
                            onClick={() => handleRevokePermission(perm.userId, perm.userEmail)}
                            disabled={permissionLoading}
                          >
                            Revoke
                          </button>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}

            <div style={{ marginTop: '20px', textAlign: 'right' }}>
              <button className="btn btn-secondary" onClick={() => setPermissionModalDoc(null)}>
                Close
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
};

export default App;
