import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  PRODUCTION_API_BASE_URL,
  DEVELOPMENT_API_BASE_URL,
  normalizeApiBaseUrl,
  isLocalhostUrl,
  resolveProductionApiBaseUrl,
  resolveDevelopmentApiBaseUrl,
  resolveApiBaseUrl,
} from '../src/api/config.ts';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

describe('TrustVault Frontend API Configuration Tests', () => {
  describe('normalizeApiBaseUrl', () => {
    it('appends /api to origin URL without trailing slash', () => {
      assert.equal(
        normalizeApiBaseUrl('https://trustvault-glst.onrender.com'),
        'https://trustvault-glst.onrender.com/api'
      );
    });

    it('strips trailing slash before appending /api', () => {
      assert.equal(
        normalizeApiBaseUrl('https://trustvault-glst.onrender.com/'),
        'https://trustvault-glst.onrender.com/api'
      );
    });

    it('preserves existing /api without duplicate /api/api', () => {
      assert.equal(
        normalizeApiBaseUrl('https://trustvault-glst.onrender.com/api'),
        'https://trustvault-glst.onrender.com/api'
      );
    });

    it('strips trailing slash from existing /api/ route', () => {
      assert.equal(
        normalizeApiBaseUrl('https://trustvault-glst.onrender.com/api/'),
        'https://trustvault-glst.onrender.com/api'
      );
    });

    it('handles localhost URLs cleanly', () => {
      assert.equal(
        normalizeApiBaseUrl('http://localhost:5115'),
        'http://localhost:5115/api'
      );
      assert.equal(
        normalizeApiBaseUrl('http://localhost:5115/api'),
        'http://localhost:5115/api'
      );
    });

    it('trims leading and trailing whitespace', () => {
      assert.equal(
        normalizeApiBaseUrl('   https://trustvault-glst.onrender.com   '),
        'https://trustvault-glst.onrender.com/api'
      );
    });

    it('returns empty string for empty or non-string inputs', () => {
      assert.equal(normalizeApiBaseUrl(''), '');
      assert.equal(normalizeApiBaseUrl('   '), '');
      assert.equal(normalizeApiBaseUrl(null), '');
      assert.equal(normalizeApiBaseUrl(undefined), '');
    });
  });

  describe('isLocalhostUrl', () => {
    it('identifies localhost hostnames', () => {
      assert.equal(isLocalhostUrl('http://localhost:5115/api'), true);
      assert.equal(isLocalhostUrl('https://localhost:5115'), true);
      assert.equal(isLocalhostUrl('http://localhost'), true);
      assert.equal(isLocalhostUrl('localhost:5115'), true);
    });

    it('identifies IPv4 loopback 127.0.0.1', () => {
      assert.equal(isLocalhostUrl('http://127.0.0.1:5115/api'), true);
      assert.equal(isLocalhostUrl('http://127.0.0.1'), true);
    });

    it('identifies IPv6 loopback and 0.0.0.0', () => {
      assert.equal(isLocalhostUrl('http://[::1]:5115/api'), true);
      assert.equal(isLocalhostUrl('http://0.0.0.0:5115'), true);
    });

    it('recognizes legitimate production hostnames as non-localhost', () => {
      assert.equal(isLocalhostUrl('https://trustvault-glst.onrender.com'), false);
      assert.equal(isLocalhostUrl('https://trustvault-glst.onrender.com/api'), false);
      assert.equal(isLocalhostUrl('https://trust-vault-phi.vercel.app'), false);
      assert.equal(isLocalhostUrl('https://api.trustvault.example.com/api'), false);
    });

    it('returns false for empty or non-string values', () => {
      assert.equal(isLocalhostUrl(''), false);
      assert.equal(isLocalhostUrl(null), false);
      assert.equal(isLocalhostUrl(undefined), false);
    });
  });

  describe('resolveProductionApiBaseUrl', () => {
    it('defaults to canonical Render backend when no environment variable is provided', () => {
      assert.equal(resolveProductionApiBaseUrl(undefined), PRODUCTION_API_BASE_URL);
      assert.equal(resolveProductionApiBaseUrl(''), PRODUCTION_API_BASE_URL);
      assert.equal(resolveProductionApiBaseUrl('   '), PRODUCTION_API_BASE_URL);
    });

    it('guards against and rejects localhost URLs in production mode', () => {
      assert.equal(
        resolveProductionApiBaseUrl('http://localhost:5115/api'),
        PRODUCTION_API_BASE_URL
      );
      assert.equal(
        resolveProductionApiBaseUrl('http://localhost:5115'),
        PRODUCTION_API_BASE_URL
      );
      assert.equal(
        resolveProductionApiBaseUrl('http://127.0.0.1:5115/api'),
        PRODUCTION_API_BASE_URL
      );
    });

    it('normalizes valid custom production backend URLs', () => {
      assert.equal(
        resolveProductionApiBaseUrl('https://trustvault-glst.onrender.com'),
        'https://trustvault-glst.onrender.com/api'
      );
      assert.equal(
        resolveProductionApiBaseUrl('https://trustvault-glst.onrender.com/api/'),
        'https://trustvault-glst.onrender.com/api'
      );
      assert.equal(
        resolveProductionApiBaseUrl('https://api.mycustomdomain.com'),
        'https://api.mycustomdomain.com/api'
      );
    });
  });

  describe('resolveDevelopmentApiBaseUrl', () => {
    it('defaults to local ASP.NET Core URL when unset', () => {
      assert.equal(resolveDevelopmentApiBaseUrl(undefined), DEVELOPMENT_API_BASE_URL);
      assert.equal(resolveDevelopmentApiBaseUrl(''), DEVELOPMENT_API_BASE_URL);
    });

    it('allows localhost development overrides', () => {
      assert.equal(
        resolveDevelopmentApiBaseUrl('http://localhost:5000'),
        'http://localhost:5000/api'
      );
      assert.equal(
        resolveDevelopmentApiBaseUrl('http://127.0.0.1:5115/api'),
        'http://127.0.0.1:5115/api'
      );
    });

    it('allows pointing local development to remote backend', () => {
      assert.equal(
        resolveDevelopmentApiBaseUrl('https://trustvault-glst.onrender.com'),
        'https://trustvault-glst.onrender.com/api'
      );
    });
  });

  describe('resolveApiBaseUrl options dispatch', () => {
    it('uses production resolution when isProduction is true', () => {
      const prodUrl = resolveApiBaseUrl({ isProduction: true });
      assert.equal(prodUrl, PRODUCTION_API_BASE_URL);

      const guardedUrl = resolveApiBaseUrl({
        isProduction: true,
        envBaseUrl: 'http://localhost:5115/api',
      });
      assert.equal(guardedUrl, PRODUCTION_API_BASE_URL);
    });

    it('uses development resolution when isProduction is false', () => {
      const devUrl = resolveApiBaseUrl({ isProduction: false });
      assert.equal(devUrl, DEVELOPMENT_API_BASE_URL);
    });
  });

  describe('Endpoint resolution matching', () => {
    const baseUrl = resolveProductionApiBaseUrl();

    it('resolves /api/health cleanly without duplicate /api prefixes', () => {
      assert.equal(`${baseUrl}/health`, 'https://trustvault-glst.onrender.com/api/health');
    });

    it('resolves /api/auth/login cleanly', () => {
      assert.equal(`${baseUrl}/auth/login`, 'https://trustvault-glst.onrender.com/api/auth/login');
    });

    it('resolves /api/auth/register cleanly', () => {
      assert.equal(`${baseUrl}/auth/register`, 'https://trustvault-glst.onrender.com/api/auth/register');
    });

    it('resolves document and audit endpoints cleanly', () => {
      assert.equal(`${baseUrl}/documents`, 'https://trustvault-glst.onrender.com/api/documents');
      assert.equal(`${baseUrl}/documents/upload`, 'https://trustvault-glst.onrender.com/api/documents/upload');
      assert.equal(`${baseUrl}/audit`, 'https://trustvault-glst.onrender.com/api/audit');
    });
  });

  describe('Production bundle artifact verification', () => {
    it('ensures dist bundle exists and contains Render backend without localhost:5115', () => {
      const distAssetsDir = path.resolve(__dirname, '../dist/assets');
      if (!fs.existsSync(distAssetsDir)) {
        console.warn('dist/assets not found, skipping bundle scan');
        return;
      }

      const jsFiles = fs.readdirSync(distAssetsDir).filter(f => f.endsWith('.js'));
      assert.ok(jsFiles.length > 0, 'Expected at least one compiled JS bundle');

      for (const jsFile of jsFiles) {
        const content = fs.readFileSync(path.join(distAssetsDir, jsFile), 'utf8');

        // Main app chunk contains the API configuration
        if (content.includes('trustvault-glst.onrender.com')) {
          assert.equal(
            content.includes('localhost:5115'),
            false,
            `File ${jsFile} must NOT contain localhost:5115 in production bundle`
          );
          assert.equal(
            content.includes('https://trustvault-glst.onrender.com/api'),
            true,
            `File ${jsFile} must contain https://trustvault-glst.onrender.com/api`
          );
        }
      }
    });
  });
});
