# TrustVault — Production Deployment Guide

> Version: 1.0.0
> Last Updated: 2026-10-22

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [Local Development Setup](#2-local-development-setup)
3. [Docker Compose Deployment](#3-docker-compose-deployment)
4. [AWS Production Deployment](#4-aws-production-deployment)
5. [Environment Variables Reference](#5-environment-variables-reference)
6. [Database Migrations](#6-database-migrations)
7. [Health Checks and Monitoring](#7-health-checks-and-monitoring)
8. [Rollback Procedures](#8-rollback-procedures)
9. [Cost Estimates](#9-cost-estimates)
10. [Cleanup Instructions](#10-cleanup-instructions)

---

## 1. Prerequisites

### Required Software

| Tool | Minimum Version | Purpose |
|---|---|---|
| .NET 8 SDK | 8.0.426+ | Backend compilation and runtime |
| Node.js | 20+ | Frontend build toolchain |
| npm | 10+ | Package management |
| Docker | 24+ | Container deployment |
| Docker Compose | 3.8+ | Multi-service orchestration |
| PostgreSQL | 16+ | Production database |
| AWS CLI | 2.x | Cloud deployment (optional) |

### Required Accounts (Production Only)

- AWS account with IAM administrative access
- Container registry access (ECR or Docker Hub)
- Domain name with DNS management access

---

## 2. Local Development Setup

### Step 1: Clone and Install

```powershell
git clone https://github.com/nagabhyruchiranjeevi/TrustVault.git
cd TrustVault

# Backend dependencies (restored automatically on build)
dotnet restore

# Frontend dependencies
cd frontend
npm install
cd ..
```

### Step 2: Configure Environment

```powershell
# SQLite mode (zero-configuration, default)
$env:DatabaseProvider = "Sqlite"

# OR PostgreSQL mode (requires running PostgreSQL)
$env:DatabaseProvider = "PostgreSQL"
$env:ConnectionStrings__DefaultConnection = "Host=localhost;Port=5432;Database=trustvault_db;Username=postgres;Password=YOUR_PASSWORD;"
```

### Step 3: Start the Backend

```powershell
cd backend/TrustVault.Api
dotnet run
```

The API starts at `http://localhost:5115`. Verify with:
```powershell
Invoke-RestMethod http://localhost:5115/api/health
```

### Step 4: Start the Frontend

```powershell
cd frontend
npm run dev
```

The frontend starts at `http://localhost:5173`.

### Step 5: Verify Tests

```powershell
dotnet test --verbosity normal
# Expected: 31/31 tests passing
```

### Default Credentials

| Account | Email | Password | Role |
|---|---|---|---|
| Admin | `admin@trustvault.local` | `Admin#TrustVault2026!` | Admin |

> [!CAUTION]
> Change the admin password immediately in production. Set `SeedData__AdminPassword` via environment variable.

---

## 3. Docker Compose Deployment

### Step 1: Review Configuration

The `docker-compose.yml` at the repository root defines:
- **PostgreSQL 16 Alpine** container with health checks
- **TrustVault API** container with environment injection
- Named volumes for data persistence

### Step 2: Build and Start

```powershell
docker-compose up -d --build
```

### Step 3: Verify

```powershell
# Check container health
docker-compose ps

# Test API endpoint
Invoke-RestMethod http://localhost:5115/api/health
```

### Step 4: Frontend Production Build

```powershell
cd frontend
npm run build
# Serve the dist/ folder with any static file server
# OR configure a reverse proxy (Nginx) to serve the SPA
```

### Docker Compose Architecture

```
┌─────────────────┐     ┌─────────────────┐
│  trustvault-api  │────▶│  trustvault-db   │
│  Port: 5115      │     │  PostgreSQL 16   │
│  .NET 8 Runtime  │     │  Port: 5432      │
└─────────────────┘     └─────────────────┘
        │                        │
   vault_storage          postgres_data
   (named volume)         (named volume)
```

---

## 4. AWS Production Deployment

> [!IMPORTANT]
> Do NOT execute these commands without explicit authorization. AWS resources incur costs.

### Architecture Overview

```
┌──────────────┐    ┌──────────────┐    ┌──────────────────────┐
│ Route 53 DNS │───▶│ ACM TLS Cert │───▶│ Application Load     │
│              │    │              │    │ Balancer (ALB)       │
└──────────────┘    └──────────────┘    │ HTTPS :443           │
                                        └──────────┬───────────┘
                                                   │
                            ┌──────────────────────┼──────────────┐
                            │          Private Subnet              │
                            │  ┌───────────────┐  ┌─────────────┐ │
                            │  │ ECS Fargate    │  │ RDS         │ │
                            │  │ TrustVault API │──│ PostgreSQL  │ │
                            │  │ 0.25 vCPU      │  │ db.t4g.micro│ │
                            │  └───────┬────────┘  └─────────────┘ │
                            │          │                           │
                            │  ┌───────▼────────┐                  │
                            │  │ S3 Private      │                  │
                            │  │ Bucket (SSE-S3) │                  │
                            │  └────────────────┘                  │
                            └──────────────────────────────────────┘
```

### Step 1: Create ECR Repository

```bash
aws ecr create-repository \
  --repository-name trustvault-api \
  --image-scanning-configuration scanOnPush=true \
  --encryption-configuration encryptionType=AES256
```

### Step 2: Build and Push Docker Image

```bash
# Authenticate Docker to ECR
aws ecr get-login-password --region us-east-1 | \
  docker login --username AWS --password-stdin <ACCOUNT_ID>.dkr.ecr.us-east-1.amazonaws.com

# Build and tag
docker build -t trustvault-api ./backend/TrustVault.Api
docker tag trustvault-api:latest <ACCOUNT_ID>.dkr.ecr.us-east-1.amazonaws.com/trustvault-api:latest

# Push to ECR
docker push <ACCOUNT_ID>.dkr.ecr.us-east-1.amazonaws.com/trustvault-api:latest
```

### Step 3: Create S3 Bucket

```bash
aws s3api create-bucket \
  --bucket trustvault-private-vault \
  --region us-east-1

aws s3api put-public-access-block \
  --bucket trustvault-private-vault \
  --public-access-block-configuration \
    BlockPublicAcls=true,IgnorePublicAcls=true,BlockPublicPolicy=true,RestrictPublicBuckets=true

aws s3api put-bucket-encryption \
  --bucket trustvault-private-vault \
  --server-side-encryption-configuration \
    '{"Rules":[{"ApplyServerSideEncryptionByDefault":{"SSEAlgorithm":"AES256"}}]}'
```

### Step 4: Create RDS Instance

```bash
aws rds create-db-instance \
  --db-instance-identifier trustvault-db \
  --db-instance-class db.t4g.micro \
  --engine postgres \
  --engine-version 16 \
  --master-username postgres \
  --master-user-password <SECURE_PASSWORD> \
  --allocated-storage 20 \
  --storage-encrypted \
  --no-publicly-accessible \
  --vpc-security-group-ids <SG_ID> \
  --db-subnet-group-name <SUBNET_GROUP>
```

### Step 5: Store Secrets

```bash
aws secretsmanager create-secret \
  --name trustvault/db-password \
  --secret-string "<SECURE_PASSWORD>"

aws secretsmanager create-secret \
  --name trustvault/jwt-secret \
  --secret-string "<MIN_32_CHAR_RANDOM_SECRET>"

aws secretsmanager create-secret \
  --name trustvault/admin-password \
  --secret-string "<STRONG_ADMIN_PASSWORD>"
```

### Step 6: Create ECS Fargate Service

```bash
# Create cluster
aws ecs create-cluster --cluster-name trustvault-cluster

# Register task definition (see task-definition.json template below)
aws ecs register-task-definition --cli-input-json file://task-definition.json

# Create service
aws ecs create-service \
  --cluster trustvault-cluster \
  --service-name trustvault-api-service \
  --task-definition trustvault-api \
  --desired-count 1 \
  --launch-type FARGATE \
  --network-configuration "awsvpcConfiguration={subnets=[<SUBNET_ID>],securityGroups=[<SG_ID>],assignPublicIp=DISABLED}" \
  --load-balancers "targetGroupArn=<TG_ARN>,containerName=trustvault-api,containerPort=8080"
```

### ECS Task Definition Template

```json
{
  "family": "trustvault-api",
  "networkMode": "awsvpc",
  "requiresCompatibilities": ["FARGATE"],
  "cpu": "256",
  "memory": "512",
  "executionRoleArn": "<EXECUTION_ROLE_ARN>",
  "taskRoleArn": "<TASK_ROLE_ARN>",
  "containerDefinitions": [
    {
      "name": "trustvault-api",
      "image": "<ACCOUNT_ID>.dkr.ecr.us-east-1.amazonaws.com/trustvault-api:latest",
      "portMappings": [{ "containerPort": 8080, "protocol": "tcp" }],
      "environment": [
        { "name": "ASPNETCORE_ENVIRONMENT", "value": "Production" },
        { "name": "DatabaseProvider", "value": "PostgreSQL" },
        { "name": "Storage__Provider", "value": "S3" },
        { "name": "Storage__S3BucketName", "value": "trustvault-private-vault" },
        { "name": "Jwt__Issuer", "value": "TrustVault" },
        { "name": "Jwt__Audience", "value": "TrustVaultUsers" }
      ],
      "secrets": [
        { "name": "ConnectionStrings__DefaultConnection", "valueFrom": "<SECRET_ARN_DB>" },
        { "name": "Jwt__SecretKey", "valueFrom": "<SECRET_ARN_JWT>" },
        { "name": "SeedData__AdminPassword", "valueFrom": "<SECRET_ARN_ADMIN>" }
      ],
      "logConfiguration": {
        "logDriver": "awslogs",
        "options": {
          "awslogs-group": "/ecs/trustvault-api",
          "awslogs-region": "us-east-1",
          "awslogs-stream-prefix": "ecs"
        }
      },
      "healthCheck": {
        "command": ["CMD-SHELL", "curl -f http://localhost:8080/api/health || exit 1"],
        "interval": 30,
        "timeout": 5,
        "retries": 3,
        "startPeriod": 60
      }
    }
  ]
}
```

### Step 7: Apply Database Migrations

```bash
# From a bastion host or local machine with VPN access to private subnet:
CONNECTION_STRING="Host=<RDS_ENDPOINT>;Port=5432;Database=trustvault_db;Username=postgres;Password=<PASSWORD>;"
dotnet ef database update \
  --project backend/TrustVault.Api/TrustVault.Api.csproj \
  --connection "$CONNECTION_STRING"
```

### Step 8: Configure Frontend Deployment

```bash
# Build frontend with production API URL
cd frontend
VITE_API_BASE_URL=https://api.yourdomain.com npm run build

# Deploy to S3 + CloudFront (or serve from ALB)
aws s3 sync dist/ s3://trustvault-frontend/ --delete
```

---

## 5. Environment Variables Reference

| Variable | Required | Default | Description |
|---|---|---|---|
| `DatabaseProvider` | No | `Sqlite` | `Sqlite` or `PostgreSQL` |
| `ConnectionStrings__DefaultConnection` | Prod | See `.env.example` | PostgreSQL connection string |
| `ConnectionStrings__SqliteConnection` | No | `Data Source=trustvault.db` | SQLite connection |
| `Jwt__Issuer` | No | `TrustVault` | JWT issuer claim |
| `Jwt__Audience` | No | `TrustVaultUsers` | JWT audience claim |
| `Jwt__SecretKey` | **Yes** | — | HMAC-SHA256 key (min 32 chars) |
| `Jwt__ExpiryMinutes` | No | `120` | Token lifetime |
| `Storage__Provider` | No | `Local` | `Local` or `S3` |
| `Storage__LocalVaultPath` | No | `SecureVaultStorage` | Local vault directory |
| `Storage__S3BucketName` | Prod | `trustvault-private-vault` | S3 bucket name |
| `SeedData__AdminEmail` | No | `admin@trustvault.local` | Initial admin email |
| `SeedData__AdminPassword` | **Yes** | — | Initial admin password |
| `ASPNETCORE_ENVIRONMENT` | No | `Development` | Runtime environment |

---

## 6. Database Migrations

### List Pending Migrations

```powershell
dotnet ef migrations list --project backend/TrustVault.Api/TrustVault.Api.csproj
```

### Apply Migrations

```powershell
dotnet ef database update --project backend/TrustVault.Api/TrustVault.Api.csproj
```

### Create New Migration

```powershell
dotnet ef migrations add <MigrationName> --project backend/TrustVault.Api/TrustVault.Api.csproj
```

### Current Migrations

| Migration | Description |
|---|---|
| `InitialCreate` | Base schema: Users, Documents, Permissions, AuditEvents |
| `AddAuditHashChainColumns` | Adds `SequenceNumber`, `PreviousHash`, `CurrentHash` to AuditEvent |

---

## 7. Health Checks and Monitoring

### Health Endpoint

```
GET /api/health
```

Returns:
```json
{
  "status": "Healthy",
  "environment": "Development",
  "version": "1.0.0",
  "databaseConnected": true,
  "timestamp": "2026-10-22T10:45:00Z"
}
```

### Recommended Monitoring

| Metric | Tool | Threshold |
|---|---|---|
| API response time | CloudWatch / ALB metrics | p99 < 500ms |
| Container CPU | ECS CloudWatch | < 80% sustained |
| Container memory | ECS CloudWatch | < 80% sustained |
| Database connections | RDS CloudWatch | < 80% max |
| 5xx error rate | ALB CloudWatch | < 0.1% |
| Audit chain integrity | `/api/Audit/verify` (cron) | Must return `isValid: true` |

---

## 8. Rollback Procedures

### Application Rollback

```bash
# Revert to previous ECS task definition revision
aws ecs update-service \
  --cluster trustvault-cluster \
  --service trustvault-api-service \
  --task-definition trustvault-api:<PREVIOUS_REVISION>
```

### Database Rollback

```powershell
# Revert to specific migration
dotnet ef database update <PreviousMigrationName> \
  --project backend/TrustVault.Api/TrustVault.Api.csproj
```

> [!WARNING]
> Database rollbacks may cause data loss if new columns contain data. Always create RDS snapshots before migrations.

---

## 9. Cost Estimates

### AWS Monthly Costs (Minimal Configuration)

| Resource | Specification | Estimated Cost |
|---|---|---|
| S3 (document storage) | ~1 GB | ~$0.03/month |
| ECS Fargate | 0.25 vCPU, 0.5 GB | ~$9–15/month |
| RDS PostgreSQL | db.t4g.micro, 20 GB | ~$15–18/month |
| ALB | 1 load balancer | ~$16/month |
| CloudWatch Logs | ~1 GB/month | ~$0.50/month |
| Secrets Manager | 3 secrets | ~$1.20/month |
| **Total** | | **~$42–51/month** |

> [!NOTE]
> RDS db.t4g.micro may be eligible for AWS Free Tier on new accounts.

---

## 10. Cleanup Instructions

### Docker Compose Cleanup

```powershell
docker-compose down -v  # Removes containers and volumes
```

### AWS Resource Cleanup

```bash
# 1. Stop ECS service
aws ecs update-service --cluster trustvault-cluster --service trustvault-api-service --desired-count 0
aws ecs delete-service --cluster trustvault-cluster --service trustvault-api-service --force

# 2. Delete ECR repository
aws ecr delete-repository --repository-name trustvault-api --force

# 3. Delete ECS cluster
aws ecs delete-cluster --cluster trustvault-cluster

# 4. Delete RDS instance (no final snapshot)
aws rds delete-db-instance --db-instance-identifier trustvault-db --skip-final-snapshot

# 5. Empty and delete S3 bucket
aws s3 rm s3://trustvault-private-vault --recursive
aws s3api delete-bucket --bucket trustvault-private-vault

# 6. Delete secrets
aws secretsmanager delete-secret --secret-id trustvault/db-password --force-delete-without-recovery
aws secretsmanager delete-secret --secret-id trustvault/jwt-secret --force-delete-without-recovery
aws secretsmanager delete-secret --secret-id trustvault/admin-password --force-delete-without-recovery

# 7. Delete CloudWatch log group
aws logs delete-log-group --log-group-name /ecs/trustvault-api
```

> [!CAUTION]
> These commands permanently destroy all data. Create backups before cleanup if you need to preserve data.
