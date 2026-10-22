import React, { useState, useEffect } from 'react';
import { api } from './api/client';
import type { User, DocumentItem, HealthCheckResponse, AuditEventItem, AuditVerificationResult } from './types';
import { Sidebar } from './components/Sidebar';
import { Header } from './components/Header';
import { AuthScreen } from './components/AuthScreen';
import { DashboardView } from './components/DashboardView';
import { DocumentsView } from './components/DocumentsView';
import { UploadView } from './components/UploadView';
import { AuditView } from './components/AuditView';
import { ManageAccessModal } from './components/ManageAccessModal';
import { EditTitleModal } from './components/EditTitleModal';
import { DeleteConfirmModal } from './components/DeleteConfirmModal';

export const App: React.FC = () => {
  // Session & User
  const [currentUser, setCurrentUser] = useState<User | null>(null);
  const [health, setHealth] = useState<HealthCheckResponse | null>(null);

  // Navigation & View
  const [activeView, setActiveView] = useState<'dashboard' | 'my-documents' | 'shared' | 'upload' | 'audit'>('dashboard');
  const [isMobileSidebarOpen, setIsMobileSidebarOpen] = useState(false);

  // Data Stores
  const [documents, setDocuments] = useState<DocumentItem[]>([]);
  const [searchQuery, setSearchQuery] = useState('');
  const [auditLogs, setAuditLogs] = useState<AuditEventItem[]>([]);
  const [auditVerification, setAuditVerification] = useState<AuditVerificationResult | null>(null);

  // Modals
  const [permissionModalDoc, setPermissionModalDoc] = useState<DocumentItem | null>(null);
  const [editTitleModalDoc, setEditTitleModalDoc] = useState<DocumentItem | null>(null);
  const [deleteModalDoc, setDeleteModalDoc] = useState<DocumentItem | null>(null);

  // Notifications & Loaders
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [successMsg, setSuccessMsg] = useState<string | null>(null);

  const checkHealth = React.useCallback(async () => {
    try {
      const res = await api.getHealth();
      setHealth(res);
    } catch {
      setHealth(null);
    }
  }, []);

  const fetchUserProfile = React.useCallback(async () => {
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
  }, []);

  const fetchDocuments = React.useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const docs = searchQuery.trim()
        ? await api.searchDocuments(searchQuery.trim())
        : await api.getDocuments();
      setDocuments(docs);
    } catch (err: any) {
      setError(err.message || 'Failed to load documents.');
    } finally {
      setLoading(false);
    }
  }, [searchQuery]);

  const fetchAuditLogs = React.useCallback(async () => {
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
      setError(err.message || 'Failed to load audit records.');
    } finally {
      setLoading(false);
    }
  }, [currentUser]);

  // Check backend health & token on mount
  useEffect(() => {
    checkHealth();
    if (api.getToken()) {
      fetchUserProfile();
    }
  }, [checkHealth, fetchUserProfile]);

  // Fetch view-specific data when activeView or user changes
  useEffect(() => {
    if (currentUser) {
      if (activeView === 'dashboard' || activeView === 'my-documents' || activeView === 'shared') {
        fetchDocuments();
      } else if (activeView === 'audit') {
        fetchAuditLogs();
      }
    }
  }, [currentUser, activeView, fetchDocuments, fetchAuditLogs]);

  const handleSearchSubmit = (e: React.FormEvent) => {
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
      setError(err.message || 'Failed to load documents.');
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
      setSuccessMsg(
        `Cryptographic audit integrity verified: ${res.totalEventsVerified} linked records valid from Genesis block.`
      );
    } catch (err: any) {
      setError(`Audit Integrity Check Failed: ${err.message}`);
    } finally {
      setLoading(false);
    }
  };

  const handleDownload = async (doc: DocumentItem) => {
    setError(null);
    try {
      await api.downloadDocument(doc.id, doc.fileName);
      setSuccessMsg(`Document "${doc.fileName}" successfully downloaded.`);
    } catch (err: any) {
      setError(`Download Denied: ${err.message}`);
    }
  };

  const handleConfirmDelete = async (doc: DocumentItem) => {
    setError(null);
    try {
      await api.deleteDocument(doc.id);
      setSuccessMsg(`Document "${doc.title}" was soft-deleted. Access has been revoked immediately.`);
      await fetchDocuments();
    } catch (err: any) {
      setError(`Failed to delete document: ${err.message}`);
    }
  };

  const handleLogout = () => {
    api.logout();
    setCurrentUser(null);
    setDocuments([]);
    setAuditLogs([]);
    setAuditVerification(null);
    setPermissionModalDoc(null);
    setEditTitleModalDoc(null);
    setDeleteModalDoc(null);
    setError(null);
    setSuccessMsg(null);
    setActiveView('dashboard');
  };

  const clearAlerts = () => {
    setError(null);
    setSuccessMsg(null);
  };

  // Auth Screen
  if (!currentUser) {
    return (
      <>
        {error && (
          <div style={{ position: 'fixed', top: '16px', right: '16px', zIndex: 1100, maxWidth: '400px' }}>
            <div className="tv-alert tv-alert-error">
              <span>{error}</span>
              <button className="tv-alert-close" onClick={clearAlerts}>✕</button>
            </div>
          </div>
        )}
        {successMsg && (
          <div style={{ position: 'fixed', top: '16px', right: '16px', zIndex: 1100, maxWidth: '400px' }}>
            <div className="tv-alert tv-alert-success">
              <span>{successMsg}</span>
              <button className="tv-alert-close" onClick={clearAlerts}>✕</button>
            </div>
          </div>
        )}
        <AuthScreen
          onAuthSuccess={(user) => {
            setCurrentUser(user);
            setSuccessMsg(`Welcome, ${user.fullName || user.email}`);
          }}
          onError={(msg) => setError(msg)}
          onClearAlerts={clearAlerts}
        />
      </>
    );
  }

  const ownedCount = documents.filter((d) => d.isOwner).length;
  const sharedCount = documents.filter((d) => !d.isOwner).length;

  return (
    <div className="trustvault-layout">
      {/* Sidebar Navigation */}
      <Sidebar
        currentUser={currentUser}
        activeView={activeView}
        setActiveView={setActiveView}
        ownedCount={ownedCount}
        sharedCount={sharedCount}
        onLogout={handleLogout}
        health={health}
        isOpenMobile={isMobileSidebarOpen}
        onCloseMobile={() => setIsMobileSidebarOpen(false)}
      />

      {/* Main Workspace Area */}
      <div className="trustvault-main">
        {/* Top Header Bar */}
        <Header
          activeView={activeView}
          searchQuery={searchQuery}
          setSearchQuery={setSearchQuery}
          onSearchSubmit={handleSearchSubmit}
          onClearSearch={handleClearSearch}
          onQuickUpload={() => setActiveView('upload')}
          onRefresh={() => {
            if (activeView === 'audit') fetchAuditLogs();
            else fetchDocuments();
          }}
          onToggleMobileMenu={() => setIsMobileSidebarOpen(!isMobileSidebarOpen)}
          loading={loading}
        />

        {/* Content Container */}
        <main className="content-body">
          {/* Notifications / Alerts */}
          {error && (
            <div className="tv-alert tv-alert-error">
              <span>⚠️ {error}</span>
              <button className="tv-alert-close" onClick={() => setError(null)} aria-label="Dismiss error">✕</button>
            </div>
          )}
          {successMsg && (
            <div className="tv-alert tv-alert-success">
              <span>✅ {successMsg}</span>
              <button className="tv-alert-close" onClick={() => setSuccessMsg(null)} aria-label="Dismiss success">✕</button>
            </div>
          )}

          {/* Current Active View */}
          {activeView === 'dashboard' && (
            <DashboardView
              currentUser={currentUser}
              documents={documents}
              onNavigate={(view) => setActiveView(view)}
              onDownload={handleDownload}
              onManageAccess={(doc) => setPermissionModalDoc(doc)}
              loading={loading}
            />
          )}

          {activeView === 'my-documents' && (
            <DocumentsView
              viewMode="my-documents"
              documents={documents}
              searchQuery={searchQuery}
              onSearchChange={setSearchQuery}
              onSearchSubmit={handleSearchSubmit}
              onClearSearch={handleClearSearch}
              onDownload={handleDownload}
              onEditTitle={(doc) => setEditTitleModalDoc(doc)}
              onManageAccess={(doc) => setPermissionModalDoc(doc)}
              onDelete={(doc) => setDeleteModalDoc(doc)}
              onUploadClick={() => setActiveView('upload')}
              loading={loading}
            />
          )}

          {activeView === 'shared' && (
            <DocumentsView
              viewMode="shared"
              documents={documents}
              searchQuery={searchQuery}
              onSearchChange={setSearchQuery}
              onSearchSubmit={handleSearchSubmit}
              onClearSearch={handleClearSearch}
              onDownload={handleDownload}
              onEditTitle={(doc) => setEditTitleModalDoc(doc)}
              onManageAccess={(doc) => setPermissionModalDoc(doc)}
              onDelete={(doc) => setDeleteModalDoc(doc)}
              onUploadClick={() => setActiveView('upload')}
              loading={loading}
            />
          )}

          {activeView === 'upload' && (
            <UploadView
              onUploadSuccess={(msg) => {
                setSuccessMsg(msg);
                setActiveView('my-documents');
                fetchDocuments();
              }}
              onError={(msg) => setError(msg)}
              onClearAlerts={clearAlerts}
            />
          )}

          {activeView === 'audit' && (
            <AuditView
              currentUser={currentUser}
              auditLogs={auditLogs}
              auditVerification={auditVerification}
              onVerifyChain={handleVerifyAuditChain}
              onRefresh={fetchAuditLogs}
              loading={loading}
            />
          )}
        </main>
      </div>

      {/* Manage Access / Permissions Modal */}
      {permissionModalDoc && (
        <ManageAccessModal
          document={permissionModalDoc}
          onClose={() => setPermissionModalDoc(null)}
          onPermissionsUpdated={fetchDocuments}
          onError={(msg) => setError(msg)}
          onSuccess={(msg) => setSuccessMsg(msg)}
        />
      )}

      {/* Edit Title Modal */}
      {editTitleModalDoc && (
        <EditTitleModal
          document={editTitleModalDoc}
          onClose={() => setEditTitleModalDoc(null)}
          onTitleUpdated={(newTitle) => {
            setDocuments((prev) =>
              prev.map((d) => (d.id === editTitleModalDoc.id ? { ...d, title: newTitle } : d))
            );
          }}
          onError={(msg) => setError(msg)}
          onSuccess={(msg) => setSuccessMsg(msg)}
        />
      )}

      {/* Delete Confirmation Modal */}
      {deleteModalDoc && (
        <DeleteConfirmModal
          document={deleteModalDoc}
          onClose={() => setDeleteModalDoc(null)}
          onConfirmDelete={handleConfirmDelete}
        />
      )}
    </div>
  );
};

export default App;
