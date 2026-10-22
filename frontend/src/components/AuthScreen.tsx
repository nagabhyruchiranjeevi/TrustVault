import React, { useState } from 'react';
import { api } from '../api/client';
import type { User } from '../types';

interface AuthScreenProps {
  onAuthSuccess: (user: User) => void;
  onError: (msg: string) => void;
  onClearAlerts: () => void;
}

export const AuthScreen: React.FC<AuthScreenProps> = ({
  onAuthSuccess,
  onError,
  onClearAlerts,
}) => {
  const [authMode, setAuthMode] = useState<'login' | 'register'>('login');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [fullName, setFullName] = useState('');
  const [role, setRole] = useState<'Owner' | 'Viewer'>('Owner');
  const [showPassword, setShowPassword] = useState(false);
  const [loading, setLoading] = useState(false);
  const [validationErrors, setValidationErrors] = useState<{ [key: string]: string }>({});

  const validate = (): boolean => {
    const errors: { [key: string]: string } = {};
    const emailRegex = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

    if (!email.trim()) {
      errors.email = 'Email address is required.';
    } else if (!emailRegex.test(email.trim())) {
      errors.email = 'Please provide a valid email address.';
    }

    if (!password) {
      errors.password = 'Password is required.';
    } else if (password.length < 8) {
      errors.password = 'Password must be at least 8 characters long.';
    }

    if (authMode === 'register') {
      if (!fullName.trim()) {
        errors.fullName = 'Full name is required.';
      }
      // Password complexity check required by ASP.NET Core Identity
      const hasUpper = /[A-Z]/.test(password);
      const hasLower = /[a-z]/.test(password);
      const hasDigit = /[0-9]/.test(password);
      const hasSpecial = /[^a-zA-Z0-9]/.test(password);
      if (!hasUpper || !hasLower || !hasDigit || !hasSpecial) {
        errors.password = 'Password requires uppercase, lowercase, digit, and a special character.';
      }
    }

    setValidationErrors(errors);
    return Object.keys(errors).length === 0;
  };

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    onClearAlerts();

    if (!validate()) {
      return;
    }

    setLoading(true);
    try {
      if (authMode === 'login') {
        const res = await api.login(email.trim(), password);
        onAuthSuccess(res.user);
      } else {
        const res = await api.register(email.trim(), password, fullName.trim(), role);
        onAuthSuccess(res.user);
      }
    } catch (err: any) {
      onError(err.message || 'Authentication failed. Please verify credentials.');
    } finally {
      setLoading(false);
    }
  };

  const switchMode = (mode: 'login' | 'register') => {
    setAuthMode(mode);
    setValidationErrors({});
    onClearAlerts();
  };

  return (
    <div className="auth-container">
      <div className="auth-card">
        {/* Branding */}
        <div className="auth-header">
          <div className="auth-logo-badge">TV</div>
          <h1 className="auth-title">TrustVault</h1>
          <p className="auth-subtitle">
            Zero-Trust Document Security & Audit Platform
          </p>
        </div>

        {/* Tab Switcher */}
        <div className="auth-tabs" role="tablist">
          <button
            type="button"
            role="tab"
            aria-selected={authMode === 'login'}
            className={`auth-tab-btn ${authMode === 'login' ? 'active' : ''}`}
            onClick={() => switchMode('login')}
          >
            Sign In
          </button>
          <button
            type="button"
            role="tab"
            aria-selected={authMode === 'register'}
            className={`auth-tab-btn ${authMode === 'register' ? 'active' : ''}`}
            onClick={() => switchMode('register')}
          >
            Create Account
          </button>
        </div>

        {/* Auth Form */}
        <form onSubmit={handleSubmit} noValidate>
          {authMode === 'register' && (
            <>
              <div className="form-group">
                <label className="form-label" htmlFor="fullName">Full Name</label>
                <input
                  id="fullName"
                  type="text"
                  placeholder="e.g. Sarah Jenkins"
                  value={fullName}
                  onChange={(e) => setFullName(e.target.value)}
                  className="form-input"
                  autoComplete="name"
                  required
                />
                {validationErrors.fullName && (
                  <div className="form-error-inline">{validationErrors.fullName}</div>
                )}
              </div>

              <div className="form-group">
                <label className="form-label" htmlFor="roleSelect">Account Role</label>
                <select
                  id="roleSelect"
                  value={role}
                  onChange={(e) => setRole(e.target.value as 'Owner' | 'Viewer')}
                  className="form-select"
                >
                  <option value="Owner">Document Owner (Deposit, manage & share documents)</option>
                  <option value="Viewer">Viewer (Read-only access to permitted files)</option>
                </select>
                <div className="form-hint">
                  {role === 'Owner'
                    ? 'Authorized to upload private documents and grant access.'
                    : 'Restricted to viewing documents specifically shared with you.'}
                </div>
              </div>
            </>
          )}

          <div className="form-group">
            <label className="form-label" htmlFor="emailInput">Work Email</label>
            <input
              id="emailInput"
              type="email"
              placeholder="user@enterprise.com"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              className="form-input"
              autoComplete="email"
              required
            />
            {validationErrors.email && (
              <div className="form-error-inline">{validationErrors.email}</div>
            )}
          </div>

          <div className="form-group">
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
              <label className="form-label" htmlFor="passwordInput">Password</label>
            </div>
            <div className="input-password-wrapper">
              <input
                id="passwordInput"
                type={showPassword ? 'text' : 'password'}
                placeholder={authMode === 'register' ? 'Min 8 chars with uppercase, digit, symbol' : 'Enter your password'}
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                className="form-input"
                autoComplete={authMode === 'login' ? 'current-password' : 'new-password'}
                required
              />
              <button
                type="button"
                className="password-toggle-btn"
                onClick={() => setShowPassword(!showPassword)}
                aria-label={showPassword ? 'Hide password' : 'Show password'}
              >
                {showPassword ? 'Hide' : 'Show'}
              </button>
            </div>
            {validationErrors.password && (
              <div className="form-error-inline">{validationErrors.password}</div>
            )}
          </div>

          <button
            type="submit"
            className="btn btn-primary btn-lg"
            style={{ width: '100%', marginTop: '10px' }}
            disabled={loading}
          >
            {loading ? (
              <span>Authenticating...</span>
            ) : authMode === 'login' ? (
              <span>Sign In to Vault</span>
            ) : (
              <span>Register Vault Account</span>
            )}
          </button>
        </form>
      </div>
    </div>
  );
};
