#!/usr/bin/env bash
#
# Creates (or updates) the Elastic Beanstalk environment for Hobio Admin.
# Run by the "Provision AWS environment" GitHub workflow, or locally with AWS CLI v2 + jq.
#
# Required:
#   ENV_NAME               e.g. hobio-admin-dev
#   VPC_ID                 the VPC your Tushpa RDS instance is in
#   SUBNETS                comma separated subnet ids in that VPC (2+ in different AZs if using a load balancer)
#   RDS_SECURITY_GROUP_ID  security group attached to the Tushpa RDS instance
# Optional:
#   AWS_REGION             default ca-central-1
#   APP_NAME               default hobio-admin
#   CERTIFICATE_ARN        ACM certificate for the portal's domain (e.g. admin.hobio.ca). With it you get an
#                          Application Load Balancer with HTTPS. Without it, a single instance on plain HTTP
#                          (testing only: the session cookie is then allowed over HTTP).
#   INSTANCE_TYPE          default t3.small
#   ASSOCIATE_PUBLIC_IP    default true (set false if SUBNETS are private subnets with a NAT gateway)
#   SECRET_IDS             comma separated Secrets Manager secret names the app may read. Empty = all secrets.
#   ADMIN_ALLOWED_EMAILS   comma separated emails allowed to sign in
#   FIREBASE_WEB_API_KEY   Firebase web API key (public value from the Firebase console)
#   ASPNETCORE_ENVIRONMENT default Production
#
set -euo pipefail

AWS_REGION="${AWS_REGION:-ca-central-1}"
APP_NAME="${APP_NAME:-hobio-admin}"
INSTANCE_TYPE="${INSTANCE_TYPE:-t3.small}"
ASSOCIATE_PUBLIC_IP="${ASSOCIATE_PUBLIC_IP:-true}"
CERTIFICATE_ARN="${CERTIFICATE_ARN:-}"
SECRET_IDS="${SECRET_IDS:-}"
ADMIN_ALLOWED_EMAILS="${ADMIN_ALLOWED_EMAILS:-}"
FIREBASE_WEB_API_KEY="${FIREBASE_WEB_API_KEY:-}"
ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Production}"
: "${ENV_NAME:?ENV_NAME is required}"
: "${VPC_ID:?VPC_ID is required}"
: "${SUBNETS:?SUBNETS is required}"
: "${RDS_SECURITY_GROUP_ID:?RDS_SECURITY_GROUP_ID is required}"

export AWS_DEFAULT_REGION="$AWS_REGION"

# Strip stray spaces/newlines pasted into the workflow inputs (a trailing space breaks ARNs).
# Keep only characters valid in AWS ids/ARNs/lists. This also drops invisible characters such as
# non-breaking or zero-width spaces that come along when copying from the AWS console.
trim() { printf '%s' "$1" | LC_ALL=C sed 's/[^A-Za-z0-9:\/._,@+=-]//g'; }
ENV_NAME="$(trim "$ENV_NAME")"
VPC_ID="$(trim "$VPC_ID")"
SUBNETS="$(trim "$SUBNETS")"
RDS_SECURITY_GROUP_ID="$(trim "$RDS_SECURITY_GROUP_ID")"
CERTIFICATE_ARN="$(trim "$CERTIFICATE_ARN")"
INSTANCE_TYPE="$(trim "$INSTANCE_TYPE")"
SECRET_IDS="$(trim "$SECRET_IDS")"

log() { echo "==> $*"; }

# AWS CLI waiters give up after ~7 minutes; a new load-balanced environment can take longer.
wait_ready() {
  local i status health
  for i in $(seq 1 120); do
    read -r status health < <(aws elasticbeanstalk describe-environments --application-name "$APP_NAME" --environment-names "$ENV_NAME" \
      --query 'Environments[?Status!=`Terminated`] | [0].[Status,Health]' --output text)
    echo "    status=${status} health=${health} ($((i * 20))s)"
    if [ "$status" = "Ready" ]; then
      return 0
    fi
    if [ "$status" = "None" ] || [ -z "$status" ]; then
      echo "Environment disappeared (creation failed). Check Elastic Beanstalk > ${ENV_NAME} > Events." >&2
      return 1
    fi
    sleep 20
  done
  echo "Timed out after 40 minutes waiting for ${ENV_NAME}. Check Elastic Beanstalk > Events." >&2
  return 1
}

ACCOUNT_ID="$(aws sts get-caller-identity --query Account --output text)"
log "Account ${ACCOUNT_ID}, region ${AWS_REGION}, application ${APP_NAME}, environment ${ENV_NAME}"

# ---------------------------------------------------------------- application
if [ "$(aws elasticbeanstalk describe-applications --application-names "$APP_NAME" --query 'length(Applications)' --output text)" = "0" ]; then
  log "Creating Elastic Beanstalk application ${APP_NAME}"
  aws elasticbeanstalk create-application --application-name "$APP_NAME" \
    --description "Hobio admin analytics portal" >/dev/null
else
  log "Application ${APP_NAME} exists"
fi

# ---------------------------------------------------------------- service role
SERVICE_ROLE="aws-elasticbeanstalk-service-role"
if ! aws iam get-role --role-name "$SERVICE_ROLE" >/dev/null 2>&1; then
  log "Creating service role ${SERVICE_ROLE}"
  aws iam create-role --role-name "$SERVICE_ROLE" --assume-role-policy-document '{
    "Version": "2012-10-17",
    "Statement": [{
      "Effect": "Allow",
      "Principal": { "Service": "elasticbeanstalk.amazonaws.com" },
      "Action": "sts:AssumeRole",
      "Condition": { "StringEquals": { "sts:ExternalId": "elasticbeanstalk" } }
    }]
  }' >/dev/null
  aws iam attach-role-policy --role-name "$SERVICE_ROLE" --policy-arn arn:aws:iam::aws:policy/service-role/AWSElasticBeanstalkEnhancedHealth
  aws iam attach-role-policy --role-name "$SERVICE_ROLE" --policy-arn arn:aws:iam::aws:policy/AWSElasticBeanstalkManagedUpdatesCustomerRolePolicy
else
  log "Service role ${SERVICE_ROLE} exists"
fi

# ---------------------------------------------------------------- instance role + profile
INSTANCE_ROLE="${APP_NAME}-ec2-role"
INSTANCE_PROFILE="${APP_NAME}-ec2-profile"
if ! aws iam get-role --role-name "$INSTANCE_ROLE" >/dev/null 2>&1; then
  log "Creating instance role ${INSTANCE_ROLE}"
  aws iam create-role --role-name "$INSTANCE_ROLE" --assume-role-policy-document '{
    "Version": "2012-10-17",
    "Statement": [{ "Effect": "Allow", "Principal": { "Service": "ec2.amazonaws.com" }, "Action": "sts:AssumeRole" }]
  }' >/dev/null
  aws iam attach-role-policy --role-name "$INSTANCE_ROLE" --policy-arn arn:aws:iam::aws:policy/AWSElasticBeanstalkWebTier
fi

# Least privilege: read only the listed secrets (names get the random "-xxxxxx" suffix AWS adds).
if [ -n "$SECRET_IDS" ]; then
  RESOURCES="$(echo "$SECRET_IDS" | tr ',' '\n' | sed 's/^ *//;s/ *$//' | grep -v '^$' | while read -r id; do
    case "$id" in
      arn:*) echo "\"${id}\"" ;;
      *) echo "\"arn:aws:secretsmanager:${AWS_REGION}:${ACCOUNT_ID}:secret:${id}-*\"" ;;
    esac
  done | paste -sd, -)"
  SECRETS_POLICY="{
    \"Version\": \"2012-10-17\",
    \"Statement\": [{ \"Effect\": \"Allow\", \"Action\": [\"secretsmanager:GetSecretValue\", \"secretsmanager:DescribeSecret\"], \"Resource\": [${RESOURCES}] }]
  }"
else
  log "SECRET_IDS is empty: the app will load every secret in the account (same as Tushpa). Consider listing them."
  SECRETS_POLICY='{
    "Version": "2012-10-17",
    "Statement": [
      { "Effect": "Allow", "Action": ["secretsmanager:GetSecretValue", "secretsmanager:DescribeSecret"], "Resource": "*" },
      { "Effect": "Allow", "Action": "secretsmanager:ListSecrets", "Resource": "*" }
    ]
  }'
fi
aws iam put-role-policy --role-name "$INSTANCE_ROLE" --policy-name "${APP_NAME}-read-secrets" --policy-document "$SECRETS_POLICY"

if ! aws iam get-instance-profile --instance-profile-name "$INSTANCE_PROFILE" >/dev/null 2>&1; then
  log "Creating instance profile ${INSTANCE_PROFILE}"
  aws iam create-instance-profile --instance-profile-name "$INSTANCE_PROFILE" >/dev/null
  aws iam add-role-to-instance-profile --instance-profile-name "$INSTANCE_PROFILE" --role-name "$INSTANCE_ROLE"
  log "Waiting for IAM to propagate"
  sleep 15
fi

# ---------------------------------------------------------------- network: app SG allowed into RDS on 1433
APP_SG_NAME="${ENV_NAME}-app"
APP_SG_ID="$(aws ec2 describe-security-groups \
  --filters "Name=vpc-id,Values=${VPC_ID}" "Name=group-name,Values=${APP_SG_NAME}" \
  --query 'SecurityGroups[0].GroupId' --output text)"
if [ "$APP_SG_ID" = "None" ] || [ -z "$APP_SG_ID" ]; then
  log "Creating security group ${APP_SG_NAME}"
  APP_SG_ID="$(aws ec2 create-security-group --group-name "$APP_SG_NAME" \
    --description "Hobio Admin instances (${ENV_NAME})" --vpc-id "$VPC_ID" --query GroupId --output text)"
  aws ec2 create-tags --resources "$APP_SG_ID" --tags Key=Name,Value="$APP_SG_NAME" Key=app,Value=hobio-admin
fi
log "App security group ${APP_SG_ID}"

if aws ec2 authorize-security-group-ingress --group-id "$RDS_SECURITY_GROUP_ID" \
  --ip-permissions "IpProtocol=tcp,FromPort=1433,ToPort=1433,UserIdGroupPairs=[{GroupId=${APP_SG_ID},Description=hobio-admin-${ENV_NAME}}]" \
  >/dev/null 2>/tmp/sg-error.txt; then
  log "Allowed ${APP_SG_ID} -> RDS (${RDS_SECURITY_GROUP_ID}) on port 1433"
elif grep -q "InvalidPermission.Duplicate" /tmp/sg-error.txt; then
  log "RDS ingress rule already present"
else
  cat /tmp/sg-error.txt >&2
  exit 1
fi

# ---------------------------------------------------------------- platform
STACK="$(aws elasticbeanstalk list-available-solution-stacks \
  --query "SolutionStacks[?contains(@, 'Amazon Linux 2023') && contains(@, '.NET 10')] | [0]" --output text)"
if [ "$STACK" = "None" ] || [ -z "$STACK" ]; then
  echo "No '.NET 10 on Amazon Linux 2023' platform found in ${AWS_REGION}. Available .NET platforms:" >&2
  aws elasticbeanstalk list-available-solution-stacks --query "SolutionStacks[?contains(@, '.NET')]" --output text >&2
  exit 1
fi
log "Platform: ${STACK}"

# ---------------------------------------------------------------- certificate check
if [ -n "$CERTIFICATE_ARN" ]; then
  CERT_INFO="$(aws acm describe-certificate --certificate-arn "$CERTIFICATE_ARN" \
    --query 'Certificate.[Status,KeyAlgorithm,DomainName]' --output text)" || {
    echo "Certificate not found: '${CERTIFICATE_ARN}'. Copy the ARN again from Certificate Manager (ca-central-1)." >&2
    exit 1
  }
  read -r CERT_STATUS CERT_KEY CERT_DOMAIN <<<"$CERT_INFO"
  log "Certificate ${CERT_DOMAIN}: ${CERT_STATUS}, ${CERT_KEY}"
  if [ "$CERT_STATUS" != "ISSUED" ]; then
    echo "Certificate is ${CERT_STATUS}; it must be ISSUED before the load balancer can use it." >&2
    exit 1
  fi
fi

# ---------------------------------------------------------------- option settings
if [ -n "$CERTIFICATE_ARN" ]; then
  ENV_TYPE="LoadBalanced"
  REQUIRE_HTTPS="true"
else
  ENV_TYPE="SingleInstance"
  REQUIRE_HTTPS="false"
  log "No CERTIFICATE_ARN: creating a single-instance HTTP environment (testing only)."
fi

SETTINGS="$(jq -n \
  --arg vpc "$VPC_ID" \
  --arg subnets "$SUBNETS" \
  --arg publicIp "$ASSOCIATE_PUBLIC_IP" \
  --arg profile "$INSTANCE_PROFILE" \
  --arg sg "$APP_SG_ID" \
  --arg instanceType "$INSTANCE_TYPE" \
  --arg envType "$ENV_TYPE" \
  --arg serviceRole "$SERVICE_ROLE" \
  --arg cert "$CERTIFICATE_ARN" \
  --arg aspnetEnv "$ASPNETCORE_ENVIRONMENT" \
  --arg region "$AWS_REGION" \
  --arg secretIds "$SECRET_IDS" \
  --arg emails "$ADMIN_ALLOWED_EMAILS" \
  --arg webKey "$FIREBASE_WEB_API_KEY" \
  --arg requireHttps "$REQUIRE_HTTPS" '
  def opt($ns; $name; $value): {Namespace: $ns, OptionName: $name, Value: $value};
  [
    opt("aws:ec2:vpc"; "VPCId"; $vpc),
    opt("aws:ec2:vpc"; "Subnets"; $subnets),
    opt("aws:ec2:vpc"; "AssociatePublicIpAddress"; $publicIp),
    opt("aws:autoscaling:launchconfiguration"; "IamInstanceProfile"; $profile),
    opt("aws:autoscaling:launchconfiguration"; "SecurityGroups"; $sg),
    opt("aws:autoscaling:launchconfiguration"; "DisableIMDSv1"; "true"),
    opt("aws:ec2:instances"; "InstanceTypes"; $instanceType),
    opt("aws:elasticbeanstalk:environment"; "EnvironmentType"; $envType),
    opt("aws:elasticbeanstalk:environment"; "ServiceRole"; $serviceRole),
    opt("aws:elasticbeanstalk:healthreporting:system"; "SystemType"; "enhanced"),
    opt("aws:elasticbeanstalk:application"; "Application Healthcheck URL"; "/healthz"),
    opt("aws:elasticbeanstalk:command"; "DeploymentPolicy"; "AllAtOnce"),
    opt("aws:elasticbeanstalk:application:environment"; "ASPNETCORE_ENVIRONMENT"; $aspnetEnv),
    opt("aws:elasticbeanstalk:application:environment"; "AwsSecrets__Enabled"; "true"),
    opt("aws:elasticbeanstalk:application:environment"; "AwsSecrets__Region"; $region),
    opt("aws:elasticbeanstalk:application:environment"; "AwsSecrets__SecretIds"; $secretIds),
    opt("aws:elasticbeanstalk:application:environment"; "Admin__AllowedEmails"; $emails),
    opt("aws:elasticbeanstalk:application:environment"; "Firebase__WebApiKey"; $webKey),
    opt("aws:elasticbeanstalk:application:environment"; "Auth__RequireHttps"; $requireHttps)
  ]
  + (if $envType == "LoadBalanced" then [
    opt("aws:elasticbeanstalk:environment"; "LoadBalancerType"; "application"),
    opt("aws:ec2:vpc"; "ELBSubnets"; $subnets),
    opt("aws:ec2:vpc"; "ELBScheme"; "public"),
    opt("aws:autoscaling:asg"; "MinSize"; "1"),
    opt("aws:autoscaling:asg"; "MaxSize"; "1"),
    opt("aws:elbv2:listener:443"; "ListenerEnabled"; "true"),
    opt("aws:elbv2:listener:443"; "Protocol"; "HTTPS"),
    opt("aws:elbv2:listener:443"; "SSLCertificateArns"; $cert),
    opt("aws:elbv2:listener:443"; "SSLPolicy"; "ELBSecurityPolicy-TLS13-1-2-2021-06"),
    opt("aws:elasticbeanstalk:environment:process:default"; "HealthCheckPath"; "/healthz"),
    opt("aws:elasticbeanstalk:environment:process:default"; "StickinessEnabled"; "true")
  ] else [] end)
')"

# ---------------------------------------------------------------- create or update
STATUS="$(aws elasticbeanstalk describe-environments --application-name "$APP_NAME" --environment-names "$ENV_NAME" \
  --query 'Environments[?Status!=`Terminated`] | [0].Status' --output text)"

if [ "$STATUS" = "None" ] || [ -z "$STATUS" ]; then
  log "Creating environment ${ENV_NAME} (takes 5-10 minutes)"
  aws elasticbeanstalk create-environment \
    --application-name "$APP_NAME" \
    --environment-name "$ENV_NAME" \
    --solution-stack-name "$STACK" \
    --option-settings "$SETTINGS" \
    --tags Key=app,Value=hobio-admin >/dev/null
  wait_ready
else
  log "Environment ${ENV_NAME} exists (status ${STATUS}); applying settings"
  wait_ready
  # EnvironmentType can't change in place; drop it on updates.
  UPDATE_SETTINGS="$(echo "$SETTINGS" | jq '[.[] | select(.OptionName != "EnvironmentType")]')"
  aws elasticbeanstalk update-environment \
    --application-name "$APP_NAME" \
    --environment-name "$ENV_NAME" \
    --option-settings "$UPDATE_SETTINGS" >/dev/null
  sleep 20
  wait_ready
fi

aws elasticbeanstalk describe-environments --application-name "$APP_NAME" --environment-names "$ENV_NAME" \
  --query 'Environments[0].{Name:EnvironmentName,Status:Status,Health:Health,URL:CNAME}' --output table

CNAME="$(aws elasticbeanstalk describe-environments --application-name "$APP_NAME" --environment-names "$ENV_NAME" \
  --query 'Environments[0].CNAME' --output text)"
log "Done. Environment URL: ${CNAME}"
if [ -n "$CERTIFICATE_ARN" ]; then
  log "Point your portal domain (e.g. admin.hobio.ca) at ${CNAME} with a CNAME record, and add that domain to Firebase > Authentication > Settings > Authorized domains."
else
  log "Add ${CNAME} to Firebase > Authentication > Settings > Authorized domains to test sign-in."
fi
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    echo "### Hobio Admin environment ready"
    echo ""
    echo "- Environment: \`${ENV_NAME}\`"
    echo "- URL: \`${CNAME}\`"
    echo "- Type: ${ENV_TYPE}"
    echo "- App security group (allowed into RDS on 1433): \`${APP_SG_ID}\`"
  } >> "$GITHUB_STEP_SUMMARY"
fi
