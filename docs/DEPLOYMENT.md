# TrustVault: Free Public Demo Deployment Guide

This guide provides end-to-end instructions for deploying a free, publicly accessible demonstration of **TrustVault** using:
- **Frontend:** Vercel (Free Hobby Tier)
- **Backend API:** Render (Free Web Service Tier)
- **Database:** Render PostgreSQL / Neon / Supabase (Free Tier)
- **Object Storage:** Cloudflare R2 / AWS S3 (Free Tier, 100% Private Storage)

---

## 1. Architecture & Provider Account Requirements

| Component | Target Host | Free Tier Allowance | Requires Account | Potential Cost Notes |
|---|---|---|---|---|
| **Frontend** | [Vercel](https://vercel.com/) | Unlimited deployments, 100GB bandwidth/mo | Yes (Free) | Free Hobby Plan |
| **Backend API** | [Render](https://render.com/) | 750 free instance hours/mo (spins down after 15m idle) | Yes (Free) | Free Web Service (Ephemeral disk) |
| **Database** | Render / Neon / Supabase | Free 1GB Postgres (Render free DB expires in 30 days; Neon is permanent free tier) | Yes (Free) | Neon or Supabase recommended for permanent free tier |
| **Storage** | Cloudflare R2 / AWS S3 | R2: 10 GB free storage, **0 egress fees**; S3: 5 GB free for 12 mos | Yes (Free) | Cloudflare R2 requires credit card for account verification, but usage under 10 GB is $0.00 |

> [!WARNING]
> **Ephemeral Disk Alert on Render Free Tier:** Render free web services do not have persistent disk storage. When the service restarts, deploys, or sleeps after 15 minutes of inactivity, local filesystem files are destroyed. For persistent document uploads in a public demo, **Private Object Storage (Cloudflare R2 or AWS S3)** must be configured (`Storage__Provider=S3`).

---

## 2. PostgreSQL Database Setup & Migrations

### Step 1: Create a PostgreSQL Instance
1. Log in to [Neon](https://neon.tech/) (recommended for permanent free tier) or [Render](https://render.com/).
2. Create a new PostgreSQL database (e.g., `trustvault-db`).
3. Copy the connection string / `DATABASE_URL`.
   - Format: `postgres://username:password@ep-sample-123.region.neon.tech/trustvault-db?sslmode=require`

### Step 2: Safe Migration Procedure
TrustVault includes automatic, non-destructive migration execution via Entity Framework Core:
- When `DatabaseProvider=PostgreSQL` or `DATABASE_URL` is set, `backend/TrustVault.Api/Data/DbInitializer.cs` automatically invokes `context.Database.MigrateAsync()` on application startup.
- The migrations create the required tables (`Users`, `Roles`, `Documents`, `DocumentPermissions`, `AuditEvents`) along with the cryptographic hash chain unique index (`IX_AuditEvents_SequenceNumber`).

#### Optional Manual Migration Verification
To inspect or apply the migrations manually via SQL script without starting the application:
```powershell
dotnet ef migrations script -p backend/TrustVault.Api -o migrate.sql -i
```
Execute `migrate.sql` using any PostgreSQL client (e.g., `psql` or DBeaver).

---

## 3. Render Backend API Deployment (Docker)

### Step 1: Create Web Service on Render
1. Log in to your [Render Dashboard](https://dashboard.render.com/).
2. Click **New +** > **Web Service**.
3. Connect your GitHub repository: `https://github.com/nagabhyruchiranjeevi/TrustVault`.
4. Configure service settings:
   - **Name:** `trustvault-api`
   - **Region:** Nearest region to your users (e.g., Ohio / Frankfurt / Singapore)
   - **Branch:** `main`
   - **Runtime:** `Docker`
   - **Root Directory:** Leave empty (uses root `Dockerfile`) OR set to `backend/TrustVault.Api` (uses `backend/TrustVault.Api/Dockerfile`).
   - **Instance Type:** `Free`

### Step 2: Configure Environment Variables
In the Render service **Environment** tab, add the following variables:

| Variable Name | Example Value | Description |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | Enables production security guards |
| `DatabaseProvider` | `PostgreSQL` | Selects Npgsql provider |
| `DATABASE_URL` | `postgres://user:pass@host:5432/dbname?sslmode=require` | Injected PostgreSQL connection URI |
| `Jwt__Issuer` | `TrustVault` | JWT Issuer claim |
| `Jwt__Audience` | `TrustVaultUsers` | JWT Audience claim |
| `Jwt__SecretKey` | `GENERATE_A_RANDOM_SECURE_KEY_OF_AT_LEAST_32_CHARACTERS` | **Required**: Random HMAC-SHA256 secret key |
| `Jwt__ExpiryMinutes` | `120` | Token validity duration |
| `SeedData__AdminEmail` | `admin@trustvault.local` | Initial admin account email |
| `SeedData__AdminPassword` | `STRONG_UNIQUE_PASSWORD_2026!` | **Required**: Production admin password |
| `Cors__AllowedOrigins` | `https://your-frontend.vercel.app` | **Required**: Vercel frontend URL |
| `Storage__Provider` | `S3` | Set to `S3` for persistent cloud storage |
| `Storage__S3BucketName` | `trustvault-private-vault` | Name of your private S3 / R2 bucket |
| `Storage__S3ServiceUrl` | `https://<account-id>.r2.cloudflarestorage.com` | Endpoint URL (for Cloudflare R2 / MinIO) |
| `Storage__S3Region` | `auto` | Bucket region (e.g., `auto` for R2, `us-east-1` for AWS) |
| `AWS_ACCESS_KEY_ID` | `YOUR_R2_OR_AWS_ACCESS_KEY` | S3 API Access Key |
| `AWS_SECRET_ACCESS_KEY` | `YOUR_R2_OR_AWS_SECRET_KEY` | S3 API Secret Key |

> [!NOTE]
> Render automatically sets the `PORT` environment variable (typically `10000`). TrustVault's `Program.cs` automatically detects `PORT` and binds to `http://+:{PORT}`.

### Step 3: Health Check Path
In Render **Settings** > **Health Check Path**, enter:
```
/api/health
```

---

## 4. Private Object Storage Setup (Cloudflare R2)

Cloudflare R2 provides a free tier with **10 GB storage and $0.00 egress fees**, making it ideal for persistent document storage without unexpected cloud bills.

### Step 1: Create a Bucket
1. Log in to [Cloudflare Dashboard](https://dash.cloudflare.com/) > **R2**.
2. Click **Create Bucket** named `trustvault-private-vault`.
3. Keep default settings (**Private**, no public access URL).

### Step 2: Generate S3 API Credentials
1. In Cloudflare R2, navigate to **Manage R2 API Tokens**.
2. Click **Create API Token** with **Object Read & Write** permissions for the `trustvault-private-vault` bucket.
3. Copy:
   - **Access Key ID**
   - **Secret Access Key**
   - **Jurisdiction-specific Endpoint URL** (e.g., `https://<account-id>.r2.cloudflarestorage.com`)
4. Add these credentials to your Render Web Service environment variables as shown in Section 3.

---

## 5. Vercel Frontend Deployment

### Step 1: Deploy to Vercel
1. Log in to [Vercel](https://vercel.com/).
2. Click **Add New...** > **Project**.
3. Import your GitHub repository: `https://github.com/nagabhyruchiranjeevi/TrustVault`.
4. Configure project settings:
   - **Framework Preset:** `Vite`
   - **Root Directory:** Click Edit and select `frontend`
   - **Build Command:** `npm run build`
   - **Output Directory:** `dist`

### Step 2: Configure Environment Variables
In the Vercel **Environment Variables** section, add:

| Key | Value | Description |
|---|---|---|
| `VITE_API_BASE_URL` | `https://trustvault-api.onrender.com/api` | The public URL of your Render backend API |

### Step 3: Deploy & Copy Domain
1. Click **Deploy**.
2. Once deployment completes, copy your live Vercel domain:
   - Example: `https://trustvault-demo.vercel.app`
3. Return to your Render Web Service, update `Cors__AllowedOrigins` with your Vercel URL, and trigger a redeploy:
   ```env
   Cors__AllowedOrigins=https://trustvault-demo.vercel.app
   ```

---

## 6. Post-Deployment Verification Checklist

Once both services are deployed, perform the following verification:

1. **Verify Backend Health:**
   ```bash
   curl -s https://<your-render-api>.onrender.com/api/health
   ```
   Expected response:
   ```json
   {
     "status": "Healthy",
     "environment": "Production",
     "checks": {
       "database": {
         "status": "Healthy",
         "error": null
       }
     }
   }
   ```

2. **Verify Frontend Loading:**
   - Open `https://<your-vercel-app>.vercel.app/` in a clean browser window.
   - Verify that the login view loads without console errors.

3. **Verify CORS & Authentication:**
   - Click **Create Account** and register a new demo owner user.
   - Confirm successful registration and JWT token receipt.

4. **Verify Document Vault & Storage:**
   - Upload a test file (e.g., a `.txt` or `.pdf` document).
   - Verify that the file appears in **My Documents** and can be downloaded.

5. **Verify Access Delegation & Instant Revocation:**
   - Grant permission to a second user.
   - Log in as the second user and verify download access.
   - Log in as owner, revoke permission, and verify that the second user is immediately blocked with **HTTP 403 Forbidden**.

6. **Verify Cryptographic Audit Hash Chaining:**
   - Log in as the administrator.
   - Navigate to **System Audit Trail** and click **Verify Cryptographic Integrity**.
   - Verify that the green confirmation banner confirms all blocks validated back to Genesis with **0 integrity violations**.

---

## 7. Troubleshooting & Rollback Instructions

### 1. Browser reports CORS error (`Access to fetch at ... has been blocked by CORS policy`)
- **Cause:** The Vercel domain is not listed in `Cors__AllowedOrigins` on Render.
- **Fix:** In Render environment variables, ensure `Cors__AllowedOrigins` matches the exact scheme and host of your frontend (e.g., `https://trustvault-demo.vercel.app` without a trailing slash).

### 2. Render service is slow to respond on first request
- **Cause:** Render free tier spins down containers after 15 minutes of inactivity. Cold starts take approximately 30–50 seconds.
- **Fix:** Wait 30 seconds for the container to wake, or configure a free external uptime monitor (such as UptimeRobot or Cron-Job.org) to ping `/api/health` every 10 minutes.

### 3. Vercel page refresh returns 404 Not Found
- **Cause:** Single Page App routing not rewriting to `index.html`.
- **Fix:** Confirm `frontend/vercel.json` exists in the repository with the rewrite rule:
  ```json
  {
    "rewrites": [{ "source": "/(.*)", "destination": "/index.html" }]
  }
  ```

### 4. Application fails to start in Production with `CRITICAL SECURITY CONFIGURATION ERROR`
- **Cause:** `Jwt__SecretKey` was omitted, is shorter than 32 characters, or is using the development default key; or `SeedData__AdminPassword` was left as default.
- **Fix:** Generate a random 64-character hex or base64 string and set it in `Jwt__SecretKey`. Set a strong custom password in `SeedData__AdminPassword`.

### 5. Rollback Procedure
If a deployment fails:
- **Vercel:** In the Vercel dashboard, go to **Deployments**, locate the last working deployment, click the three dots, and select **Promote to Production**.
- **Render:** In the Render dashboard, go to **Deploys**, select the previous successful build, and click **Rollback**.
