# Hobio Admin

A private web portal for Hobio's founders: growth, marketplace and revenue numbers from the Tushpa database,
with a printable investor report.

- **Overview**: new users, sign-ups over time, events held, bookings, active hosts, fill rate, gross booking value,
  Hobio fee revenue, average paid event price and take rate, each compared with the previous period. Pick 1, 2, 3 or
  6 months or 1 year, and filter by city.
- **Cities & weekdays**: which days events happen in each city (local time), plus a per-city summary.
- **Retention**: for each sign-up month, the share of new users who booked in that month and the next three.
- **Investor report**: a one-page summary (headline numbers, 12-month table, top cities, retention) with
  *Print / Save as PDF*.

Built with .NET 10 Blazor Server and MudBlazor, in Hobio's colors and logo from hobio.ca. It reads Tushpa's database through
**read-only SQL views** using a **read-only login**, and never writes to it.

```
src/HobioAdmin.Web      the portal
src/HobioAdmin.DbTool   applies the SQL views and creates the read-only login
db/                     01_analytics_views.sql, 02_readonly_user.sql
tests/                  unit, database and page tests (run in CI against a real SQL Server)
scripts/aws/            create-environment.sh (Elastic Beanstalk environment)
.github/workflows/      ci-cd.yml, provision-aws.yml, db-apply.yml, screenshots.yml
```

## How sign-in works

1. The login page signs in with **Firebase** (Google, or email and password) in the browser, using the same
   Firebase project as the Hobio app.
2. The browser posts the Firebase ID token to the server. The server verifies it with the **Firebase Admin SDK**,
   using the `FirebaseAdminSdk` service account Tushpa already keeps in AWS Secrets Manager.
3. Only **verified emails on the allow-list** (`Admin__AllowedEmails`) get in. The server sets its own
   HTTP-only session cookie, valid for 8 hours.
4. The allow-list is checked on every request, so removing an email locks that person out immediately.

The app has no other way in.

## One-time setup

Do these in order. Dev first; repeat for Prod with the Prod values.

### 1. Firebase

1. Firebase console → **Project settings → General**: copy the **Web API key**. It's public, not a secret.
2. **Authentication → Sign-in method**: make sure **Google** and/or **Email/Password** are enabled.
3. **Authentication → Settings → Authorized domains**: add the portal's domain, for example `admin.hobio.ca`, or
   the Elastic Beanstalk URL while you're testing. Step 6 prints it.
4. Admins sign in with a Google account, or a Firebase email/password user whose email is **verified**.

### 2. Database: views and read-only login

The views live in a new `analytics` schema in the Tushpa database. Tushpa's own dacpac deploy leaves them alone.

**Option A: GitHub workflow.** Use this if the self-hosted runner that deploys Tushpa's database can be shared with this repo:
GitHub → **Settings → Actions → Runners**. Add these **secrets** to the `Dev` environment:
`AWS_RDS_ENDPOINT`, `AWS_RDS_DBNAME`, `AWS_RDS_USERNAME`, `AWS_RDS_PASSWORD` (same values as in Tushpa) and a new
`HOBIO_READER_PASSWORD` (16+ characters). Then run **Actions → Apply analytics views and read-only login → Dev → all**.

**Option B: by hand.** Run this from any machine that can reach the RDS instance:

```bash
export HOBIO_DB_CONNECTION="Server=<rds-endpoint>,1433;Database=<db>;User Id=<admin>;Password=<pw>;Encrypt=True;TrustServerCertificate=True"
export HOBIO_READER_PASSWORD="<new strong password>"
dotnet run --project src/HobioAdmin.DbTool -- all
```

You can also open `db/01_analytics_views.sql` in SSMS or Azure Data Studio and run it. For `db/02_readonly_user.sql`,
run part 1 in `master` and part 2 in the Tushpa database, replacing the `$(...)` values.

Either way, it's safe to re-run.

### 3. AWS secret for the portal

In **Secrets Manager (ca-central-1)**, create a secret, for example `hobio-admin/dev`, with this JSON:

```json
{
  "HobioAdmin__ReadOnlyConnectionString": "Server=<rds-endpoint>,1433;Database=<db>;User Id=hobio_admin_reader;Password=<HOBIO_READER_PASSWORD>;Encrypt=True;TrustServerCertificate=True;ApplicationIntent=ReadOnly"
}
```

The portal also needs `FirebaseAdminSdk`, which already sits in one of Tushpa's secrets. Find that secret's name.
Both secret names go into `AWS_SECRET_IDS` in step 5, comma separated.

> Tushpa's API loads *every* secret in the account, so it will also see this new one. That's harmless, because Tushpa
> never reads `HobioAdmin__*` keys.

### 4. HTTPS certificate (recommended)

In **Certificate Manager (ca-central-1)**, request a public certificate for the portal domain, for example
`admin.hobio.ca`, and validate it by DNS. Copy its ARN. Without a certificate you get a single HTTP-only
server, which is fine for a first test but not for real use.

### 5. GitHub environments

Go to GitHub → **Settings → Environments**, create `Dev` (and `Prod`), and add:

| Kind | Name | Value |
|---|---|---|
| secret | `AWS_ROLE_ARN` | IAM role GitHub assumes via OIDC (see below) |
| variable | `EB_APP_NAME` | `hobio-admin` (optional) |
| variable | `EB_ENV_NAME` | `hobio-admin-dev` (`hobio-admin-prod` for Prod) |
| variable | `ADMIN_ALLOWED_EMAILS` | e.g. `vardan@…,artur@…` |
| variable | `FIREBASE_WEB_API_KEY` | from step 1 |
| variable | `AWS_SECRET_IDS` | e.g. `hobio-admin/dev,<tushpa secret with FirebaseAdminSdk>` |

**The AWS role.** You can reuse the role Tushpa's GitHub Actions use. Edit its **trust policy** so the
`token.actions.githubusercontent.com:sub` condition also allows `repo:art-hov/hobio-admin:*`. The role needs:

- **To deploy:** Elastic Beanstalk, S3 (the Elastic Beanstalk bucket) and CloudFormation.
- **For the one-time provisioning:** also IAM (create roles and instance profiles, `iam:PassRole`), EC2 security groups,
  Auto Scaling and Elastic Load Balancing.

The simplest option is the `AdministratorAccess-AWSElasticBeanstalk` managed policy, plus IAM role and
instance-profile permissions.

### 6. Create the AWS environment

Go to **Actions → Provision AWS environment → Run workflow** and fill in:

- **environment:** `Dev`, **env_name** `hobio-admin-dev`
- **vpc_id, subnets:** the VPC of the Tushpa RDS instance, and 2+ subnets in different availability zones
  (RDS console → your database → *Connectivity & security*)
- **rds_security_group_id:** the RDS instance's security group. The script lets the portal in on port 1433.
- **certificate_arn:** from step 4, or leave empty for an HTTP test server

The script creates:

- the Elastic Beanstalk application and environment (.NET 10 on Amazon Linux 2023)
- an instance role that can read **only** the listed secrets
- a security group that's allowed into RDS

It prints the environment URL in the run summary. Point a CNAME for `admin.hobio.ca` at that URL, and add the
domain to Firebase's authorized domains (step 1).

**Cost:** one `t3.small` instance, roughly US$15/month. With HTTPS, add an Application Load Balancer at roughly
US$20/month. Check current AWS pricing.

### 7. Deploy

Every push to `main` builds, runs all tests and deploys to **Dev**. Pushes to `release` deploy to **Prod**.
If `AWS_ROLE_ARN` or `EB_ENV_NAME` isn't set yet, the deploy step is skipped with a notice.

## Run locally

```bash
cd src/HobioAdmin.Web
dotnet user-secrets set "HobioAdmin:ReadOnlyConnectionString" "<reader connection string>"
dotnet user-secrets set "Admin:AllowedEmails" "you@example.com"
dotnet user-secrets set "Firebase:WebApiKey" "<web api key>"
dotnet user-secrets set "FirebaseAdminSdk" "$(cat path/to/service-account.json)"
dotnet run    # http://localhost:5180 (add "localhost" to Firebase authorized domains)
```

Alternatively, set `AwsSecrets:Enabled=true` and `AwsSecrets:SecretIds` to read the same secrets from AWS with your
local AWS profile.

## Tests

`dotnet test` runs the unit tests. The database and page tests need a SQL Server: set `HOBIO_TEST_SQL` to an admin
connection string. CI starts one in Docker, creates the Tushpa tables, applies the real views, seeds known data
and checks every number. It also checks that the read-only login can't read `dbo` tables.

**Actions → Screenshots** renders every page in a real browser and pushes the images to the `ci-screenshots` branch.

## How the numbers are defined

- **Events held:** events whose start time falls in the period, excluding cancelled and draft events.
  Weekday and date use the event's local time zone.
- **Bookings:** player bookings made in the period, excluding cancellations and the host's own auto-booked spot.
- **Gross booking value:** what players paid, fees included. Refunded, refund-pending and credited transactions are
  excluded and reported separately.
- **Hobio fee revenue:** the `HobioFeeAmount` of those transactions. **Take rate** = fee revenue ÷ gross booking value.
- **Fill rate:** booked spots ÷ offered spots in events held.
- **Repeat bookers:** players with 2+ bookings in the period ÷ players with any booking.
- **Dates:** sign-up, booking and payment dates use Toronto time.

If Tushpa's tables change, update `db/01_analytics_views.sql`, re-run step 2, and update
`tests/HobioAdmin.Tests/Sql/tushpa-schema-fixture.sql` so CI keeps checking the real shape.
