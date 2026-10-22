import React from 'react';

interface HeaderProps {
  activeView: 'dashboard' | 'my-documents' | 'shared' | 'upload' | 'audit';
  searchQuery: string;
  setSearchQuery: (query: string) => void;
  onSearchSubmit: (e: React.FormEvent) => void;
  onClearSearch: () => void;
  onQuickUpload: () => void;
  onRefresh: () => void;
  onToggleMobileMenu: () => void;
  loading: boolean;
}

export const Header: React.FC<HeaderProps> = ({
  activeView,
  searchQuery,
  setSearchQuery,
  onSearchSubmit,
  onClearSearch,
  onQuickUpload,
  onRefresh,
  onToggleMobileMenu,
  loading,
}) => {
  const getViewTitle = () => {
    switch (activeView) {
      case 'dashboard':
        return 'Overview & Workspace';
      case 'my-documents':
        return 'My Vaulted Documents';
      case 'shared':
        return 'Documents Shared With Me';
      case 'upload':
        return 'Deposit Document';
      case 'audit':
        return 'Security & Audit Verification';
      default:
        return 'Dashboard';
    }
  };

  return (
    <header className="trustvault-header">
      <div className="header-left">
        <button
          className="mobile-menu-toggle"
          onClick={onToggleMobileMenu}
          aria-label="Toggle navigation menu"
        >
          ☰
        </button>

        <div className="breadcrumb-trail">
          <span>TrustVault</span>
          <span>/</span>
          <span className="breadcrumb-active">{getViewTitle()}</span>
        </div>
      </div>

      <div className="header-right">
        {/* Global Search Bar if on document views */}
        {(activeView === 'my-documents' || activeView === 'shared' || activeView === 'dashboard') && (
          <form onSubmit={onSearchSubmit} style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
            <div style={{ position: 'relative', display: 'flex', alignItems: 'center' }}>
              <input
                type="text"
                placeholder="Search documents..."
                value={searchQuery}
                onChange={(e) => setSearchQuery(e.target.value)}
                className="form-input"
                style={{
                  width: '240px',
                  padding: '7px 30px 7px 12px',
                  fontSize: '13px',
                  borderRadius: 'var(--radius-md)',
                }}
              />
              {searchQuery && (
                <button
                  type="button"
                  onClick={onClearSearch}
                  style={{
                    position: 'absolute',
                    right: '8px',
                    background: 'transparent',
                    border: 'none',
                    color: 'var(--text-muted)',
                    cursor: 'pointer',
                    fontSize: '14px',
                  }}
                  title="Clear search"
                >
                  ✕
                </button>
              )}
            </div>
            <button type="submit" className="btn btn-secondary btn-sm" disabled={loading}>
              Search
            </button>
          </form>
        )}

        <button
          className="btn btn-secondary btn-sm"
          onClick={onRefresh}
          disabled={loading}
          title="Refresh current data"
        >
          <span>🔄</span>
          <span>{loading ? 'Refreshing...' : 'Refresh'}</span>
        </button>

        {activeView !== 'upload' && (
          <button className="btn btn-primary btn-sm" onClick={onQuickUpload}>
            <span>➕</span>
            <span>Deposit File</span>
          </button>
        )}
      </div>
    </header>
  );
};
