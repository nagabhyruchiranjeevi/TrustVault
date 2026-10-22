import React from 'react';
import type { User, HealthCheckResponse } from '../types';

interface SidebarProps {
  currentUser: User;
  activeView: 'dashboard' | 'my-documents' | 'shared' | 'upload' | 'audit';
  setActiveView: (view: 'dashboard' | 'my-documents' | 'shared' | 'upload' | 'audit') => void;
  ownedCount: number;
  sharedCount: number;
  onLogout: () => void;
  health: HealthCheckResponse | null;
  isOpenMobile: boolean;
  onCloseMobile: () => void;
}

export const Sidebar: React.FC<SidebarProps> = ({
  currentUser,
  activeView,
  setActiveView,
  ownedCount,
  sharedCount,
  onLogout,
  health,
  isOpenMobile,
  onCloseMobile,
}) => {
  const isAdmin = currentUser.roles.includes('Admin');
  const userInitials = (currentUser.fullName || currentUser.email || 'U')
    .split(' ')
    .map((n) => n[0])
    .join('')
    .substring(0, 2)
    .toUpperCase();

  const handleNavClick = (view: 'dashboard' | 'my-documents' | 'shared' | 'upload' | 'audit') => {
    setActiveView(view);
    onCloseMobile();
  };

  return (
    <aside className={`trustvault-sidebar ${isOpenMobile ? 'sidebar-open' : ''}`}>
      {/* Brand Header */}
      <div className="sidebar-header">
        <div className="brand-icon-shield">TV</div>
        <div className="brand-text">
          <span className="brand-title">TrustVault</span>
          <span className="brand-subtitle">Enterprise Security</span>
        </div>
      </div>

      {/* Navigation Links */}
      <nav className="sidebar-nav">
        <div className="nav-section-label">Workspace</div>

        <button
          className={`nav-item-btn ${activeView === 'dashboard' ? 'active' : ''}`}
          onClick={() => handleNavClick('dashboard')}
        >
          <span className="nav-item-icon">📊</span>
          <span>Dashboard</span>
        </button>

        <button
          className={`nav-item-btn ${activeView === 'my-documents' ? 'active' : ''}`}
          onClick={() => handleNavClick('my-documents')}
        >
          <span className="nav-item-icon">📁</span>
          <span>My Documents</span>
          <span className="nav-badge">{ownedCount}</span>
        </button>

        <button
          className={`nav-item-btn ${activeView === 'shared' ? 'active' : ''}`}
          onClick={() => handleNavClick('shared')}
        >
          <span className="nav-item-icon">👥</span>
          <span>Shared With Me</span>
          <span className="nav-badge">{sharedCount}</span>
        </button>

        <button
          className={`nav-item-btn ${activeView === 'upload' ? 'active' : ''}`}
          onClick={() => handleNavClick('upload')}
        >
          <span className="nav-item-icon">📤</span>
          <span>Deposit Document</span>
        </button>

        <div className="nav-section-label" style={{ marginTop: '16px' }}>
          Governance & Security
        </div>

        <button
          className={`nav-item-btn ${activeView === 'audit' ? 'active' : ''}`}
          onClick={() => handleNavClick('audit')}
        >
          <span className="nav-item-icon">🛡️</span>
          <span>{isAdmin ? 'System Audit Trail' : 'Activity Trail'}</span>
          {isAdmin && <span className="nav-badge" style={{ color: '#f87171' }}>Admin</span>}
        </button>
      </nav>

      {/* Sidebar Footer with Health & User Profile */}
      <div className="sidebar-footer">
        {/* System Health Status */}
        <div
          className={`health-status-indicator ${
            health?.status === 'Healthy' ? 'health-healthy' : 'health-degraded'
          }`}
          style={{ width: '100%', justifyContent: 'center' }}
        >
          <span className="health-dot"></span>
          <span>Vault Core: {health ? health.status : 'Connecting...'}</span>
        </div>

        {/* User Profile Card */}
        <div className="user-profile-card">
          <div className="user-avatar-circle">{userInitials}</div>
          <div className="user-info-text">
            <div className="user-name" title={currentUser.fullName || currentUser.email}>
              {currentUser.fullName || currentUser.email}
            </div>
            <div style={{ fontSize: '11px', color: 'var(--text-muted)' }} title={currentUser.email}>
              {currentUser.email}
            </div>
            <span
              className={`user-role-badge ${
                isAdmin ? 'role-admin' : currentUser.roles.includes('Owner') ? 'role-owner' : 'role-viewer'
              }`}
            >
              {currentUser.roles.join(', ') || 'User'}
            </span>
          </div>
        </div>

        {/* Sign Out Button */}
        <button
          className="btn btn-secondary btn-sm"
          onClick={onLogout}
          style={{ width: '100%', gap: '6px' }}
        >
          <span>🚪</span>
          <span>Sign Out</span>
        </button>
      </div>
    </aside>
  );
};
