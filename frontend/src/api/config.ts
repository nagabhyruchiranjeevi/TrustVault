/**
 * TrustVault API Configuration
 *
 * Provides a single, consistent API base URL resolution across development and production environments.
 * Prevents development or localhost URLs from leaking into production builds.
 */

export const PRODUCTION_API_BASE_URL = 'https://trustvault-glst.onrender.com/api';
export const DEVELOPMENT_API_BASE_URL = 'http://localhost:5115/api';

/**
 * Checks whether a given URL points to a localhost / loopback address.
 */
export function isLocalhostUrl(url: string): boolean {
  if (!url || typeof url !== 'string') return false;
  try {
    const normalized = url.includes('://') ? url : `http://${url}`;
    const parsed = new URL(normalized);
    const hostname = parsed.hostname.toLowerCase();
    return (
      hostname === 'localhost' ||
      hostname === '127.0.0.1' ||
      hostname === '[::1]' ||
      hostname === '0.0.0.0' ||
      hostname.endsWith('.localhost')
    );
  } catch {
    return /localhost|127\.0\.0\.1|\[::1\]/i.test(url);
  }
}

/**
 * Normalizes an API base URL:
 * - Trims whitespace
 * - Strips trailing slashes
 * - Ensures it cleanly ends with '/api' without producing duplicate '/api/api'
 */
export function normalizeApiBaseUrl(rawUrl: string): string {
  if (!rawUrl || typeof rawUrl !== 'string') {
    return '';
  }

  let url = rawUrl.trim();
  while (url.endsWith('/')) {
    url = url.slice(0, -1);
  }

  if (!url) return '';

  if (!url.endsWith('/api')) {
    url = `${url}/api`;
  }

  return url;
}

function getImportMetaEnv(key: 'VITE_API_BASE_URL' | 'VITE_API_URL'): string | undefined {
  try {
    // In Vite, import.meta.env is replaced at build time
    if (key === 'VITE_API_BASE_URL') {
      return import.meta.env.VITE_API_BASE_URL;
    }
    if (key === 'VITE_API_URL') {
      return import.meta.env.VITE_API_URL;
    }
  } catch {
    // In Node test runner or non-Vite environments, import.meta.env is undefined
  }
  return undefined;
}

function isProdEnvironment(): boolean {
  try {
    return Boolean(import.meta.env.PROD);
  } catch {
    return false;
  }
}

/**
 * Resolves the API base URL for production environments.
 * - Always targets the production backend.
 * - If an explicit environment variable is provided, validates and normalizes it.
 * - Strictly rejects localhost / loopback URLs in production, falling back to canonical Render backend.
 */
export function resolveProductionApiBaseUrl(envUrl?: string): string {
  const candidate = envUrl !== undefined
    ? envUrl
    : (getImportMetaEnv('VITE_API_BASE_URL') || getImportMetaEnv('VITE_API_URL'));

  if (candidate && typeof candidate === 'string' && candidate.trim().length > 0) {
    const normalized = normalizeApiBaseUrl(candidate);
    if (!isLocalhostUrl(normalized)) {
      return normalized;
    }
    console.warn(
      `[TrustVault] Blocked localhost API URL in production environment: "${candidate}". Using production backend: "${PRODUCTION_API_BASE_URL}".`
    );
  }

  return PRODUCTION_API_BASE_URL;
}

/**
 * Resolves the API base URL for development environments.
 * - Defaults to local ASP.NET Core API at 'http://localhost:5115/api'.
 * - Accepts developer-specified environment overrides.
 */
export function resolveDevelopmentApiBaseUrl(envUrl?: string): string {
  const candidate = envUrl !== undefined
    ? envUrl
    : (getImportMetaEnv('VITE_API_BASE_URL') || getImportMetaEnv('VITE_API_URL'));

  if (candidate && typeof candidate === 'string' && candidate.trim().length > 0) {
    return normalizeApiBaseUrl(candidate);
  }

  return DEVELOPMENT_API_BASE_URL;
}

export interface ResolveApiBaseUrlOptions {
  envBaseUrl?: string;
  isProduction?: boolean;
}

/**
 * Universal resolver used across tests and runtime.
 */
export function resolveApiBaseUrl(options?: ResolveApiBaseUrlOptions): string {
  const isProd = options?.isProduction !== undefined
    ? options.isProduction
    : isProdEnvironment();

  return isProd
    ? resolveProductionApiBaseUrl(options?.envBaseUrl)
    : resolveDevelopmentApiBaseUrl(options?.envBaseUrl);
}

/**
 * The canonical API base URL used by all API client requests.
 * Evaluates production resolver when built for production.
 */
export const API_BASE_URL: string = import.meta.env?.PROD
  ? resolveProductionApiBaseUrl()
  : resolveDevelopmentApiBaseUrl();
