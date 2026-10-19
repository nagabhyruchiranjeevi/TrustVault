import React, { useState, useEffect } from 'react';
import { api } from './api/client';
import type { User, DocumentItem, HealthCheckResponse, AuditEventItem } from './types';

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
  const [auditLogs, setAuditLogs] = useState<AuditEventItem[]>([]);
  const [uploadTitle, setUploadTitle] = useState('');
  const [selectedFile, setSelectedFile] = useState<File | null>(null);

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

  // Fetch data when activeTab changes
  useEffect(() => {
    if (currentUser) {
      if (activeTab === 'documents') {
        fetchDocuments();
      } else if (activeTab === 'audit' && currentUser.roles.includes('Admin')) {
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
    } catch (err: any) {
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
      const logs = await api.getAuditLogs();
      setAuditLogs(logs);
    } catch (err: any) {
      setError(err.message || 'Failed to load audit logs');
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
        setSuccessMsg('Welcome back!');
      } else {
        const res = await api.register(email, password, fullName, role);
        setCurrentUser(res.user);
        setSuccessMsg('Account created successfully!');
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
          setSuccessMsg(`Document "${uploadTitle}" safely vaulted!`);
          setUploadTitle('');
          setSelectedFile(null);
          setActiveTab('documents');
        } catch (uploadErr: any) {
          setError(uploadErr.message || 'Failed to save document');
        } finally {
          setLoading(false);
        }
      };
      reader.readAsDataURL(selectedFile);
    } catch (err: any) {
      setError('Error reading file data');
      setLoading(false);
    }
  };

  const handleDownload = async (doc: DocumentItem) => {
    try {
      await api.downloadDocument(doc.id, doc.fileName);
    } catch (err: any) {
      setError(`Failed to download ${doc.fileName}: ${err.message}`);
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
              <span className="logo-tag">DAY 1 CORE</span>
            </div>
            <div style={{ fontSize: '12px', color: 'var(--text-muted)' }}>
              Zero-Trust Secure Document Vault
            </div>
          </div>
        </div>

        <div style={{ display: 'flex', alignItems: 'center', gap: '16px' }}>
          {/* Health indicator */}
          <div className={`badge ${health?.status === 'Healthy' ? 'badge-healthy' : 'badge-degraded'}`}>
            Backend: {health ? health.status : 'Connecting...'}
          </div>

          {currentUser && (
            <div style={{ display: 'flex', alignItems: 'center', gap: '12px' }}>
              <div style={{ textAlign: 'right' }}>
                <div style={{ fontSize: '14px', fontWeight: 600 }}>{currentUser.fullName}</div>
                <div style={{ fontSize: '12px', color: 'var(--text-muted)' }}>{currentUser.email}</div>
              </div>
              <span className={`badge ${currentUser.roles.includes('Admin') ? 'badge-admin' : 'badge-owner'}`}>
                {currentUser.roles[0] || 'User'}
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
                      <option value="Viewer">Viewer (Read-only on shared items)</option>
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
            {currentUser.roles.includes('Admin') && (
              <button
                className={`tab-btn ${activeTab === 'audit' ? 'active' : ''}`}
                onClick={() => setActiveTab('audit')}
              >
                Security Audit Log
              </button>
            )}
          </div>

          {/* Tab 1: Documents */}
          {activeTab === 'documents' && (
            <div className="glass-panel" style={{ padding: '24px' }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '16px' }}>
                <h3 style={{ fontSize: '18px', fontWeight: 600 }}>Your Secure Documents</h3>
                <button className="btn btn-secondary" onClick={fetchDocuments} disabled={loading}>
                  Refresh
                </button>
              </div>

              {loading && documents.length === 0 ? (
                <div style={{ padding: '40px', textAlign: 'center', color: 'var(--text-muted)' }}>
                  Loading secure documents...
                </div>
              ) : documents.length === 0 ? (
                <div style={{ padding: '40px', textAlign: 'center', color: 'var(--text-muted)' }}>
                  No documents found in your vault. Click "Upload Document" to protect your first file.
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
                            <button
                              className="btn btn-secondary"
                              style={{ padding: '4px 10px', fontSize: '12px' }}
                              onClick={() => handleDownload(doc)}
                            >
                              Download
                            </button>
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
                    Files are stored in private non-web-accessible vault storage with unguessable keys.
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
          {activeTab === 'audit' && currentUser.roles.includes('Admin') && (
            <div className="glass-panel" style={{ padding: '24px' }}>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: '16px' }}>
                <div>
                  <h3 style={{ fontSize: '18px', fontWeight: 600 }}>Security Audit Trail</h3>
                  <p style={{ fontSize: '13px', color: 'var(--text-muted)' }}>
                    Admins can view system activity but cannot read user documents without explicit permission.
                  </p>
                </div>
                <button className="btn btn-secondary" onClick={fetchAuditLogs} disabled={loading}>
                  Refresh Logs
                </button>
              </div>

              <div className="table-container">
                <table>
                  <thead>
                    <tr>
                      <th>Timestamp (UTC)</th>
                      <th>Action</th>
                      <th>Entity</th>
                      <th>User</th>
                      <th>Status</th>
                      <th>Details</th>
                    </tr>
                  </thead>
                  <tbody>
                    {auditLogs.map((log) => (
                      <tr key={log.id}>
                        <td style={{ fontSize: '12px', color: 'var(--text-muted)' }}>
                          {new Date(log.timestampUtc).toLocaleString()}
                        </td>
                        <td style={{ fontWeight: 600 }}>{log.action}</td>
                        <td>{log.entityType}</td>
                        <td>{log.userEmail || 'Anonymous'}</td>
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
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
};

export default App;
