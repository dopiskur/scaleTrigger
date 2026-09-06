@allowed(['Sqlite', 'MsSql'])
param databaseProvider string = 'Sqlite'

param sqlServerFqdn string = ''
param sqlDatabaseName string = ''
param sqlAdminUsername string = ''

@secure()
param sqlAdminPassword string = ''

param authUsername string

@secure()
param authPassword string

param retryMaxAttempts int = 5
param retryDelaySeconds int = 10
param dotnetChannel string = '10.0'
param repositoryUrl string = 'https://github.com/dopiskur/scaleTrigger.git'

var mssqlConnectionString = 'Server=tcp:${sqlServerFqdn},1433;Database=${sqlDatabaseName};User ID=${sqlAdminUsername};Password=${sqlAdminPassword};Encrypt=True;TrustServerCertificate=False;'
var dbEnvLinesMssql = 'Environment="DatabaseProvider=MsSql"\nEnvironment="ConnectionStrings__MsSql=${mssqlConnectionString}"'
var dbEnvLines = databaseProvider == 'MsSql' ? dbEnvLinesMssql : ''

var scriptTemplate = '''#!/bin/bash
set -e

retry() {
  local n=0
  until "$@"; do
    n=$((n+1))
    if [ "$n" -ge __RETRY_MAX__ ]; then
      return 1
    fi
    sleep __RETRY_DELAY__
  done
}

wait_for_apt_lock() {
  while fuser /var/lib/dpkg/lock-frontend >/dev/null 2>&1; do
    sleep 5
  done
}

wait_for_apt_lock
retry apt-get update
retry apt-get install -y wget git nginx openssl

retry wget https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh
chmod +x /tmp/dotnet-install.sh
retry /tmp/dotnet-install.sh --channel __DOTNET_CHANNEL__ --install-dir /usr/share/dotnet
ln -sf /usr/share/dotnet/dotnet /usr/bin/dotnet

retry git clone __REPOSITORY_URL__ /opt/scaletrigger
cp /opt/scaletrigger/ScaleTrigger/appsettings.json.example /opt/scaletrigger/ScaleTrigger/appsettings.json

cd /opt/scaletrigger/ScaleTrigger
retry dotnet publish -c Release -o /opt/scaletrigger/publish

cat > /etc/systemd/system/scaletrigger.service <<'UNIT'
[Unit]
Description=ScaleTrigger
After=network.target

[Service]
WorkingDirectory=/opt/scaletrigger/publish
ExecStart=/usr/share/dotnet/dotnet /opt/scaletrigger/publish/ScaleTrigger.dll
Restart=always
RestartSec=5
Environment=ASPNETCORE_URLS=http://127.0.0.1:5000
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment="Auth__Enabled=true"
Environment="AdminUser__Username=__AUTH_USERNAME__"
Environment="AdminUser__Password=__AUTH_PASSWORD__"
Environment="ForwardedHeaders__KnownProxies=127.0.0.1"
__DB_ENV_LINES__

[Install]
WantedBy=multi-user.target
UNIT

systemctl daemon-reload
systemctl enable scaletrigger
systemctl start scaletrigger

mkdir -p /etc/nginx/ssl
openssl req -x509 -nodes -days 365 -newkey rsa:2048 \
  -keyout /etc/nginx/ssl/scaletrigger.key \
  -out /etc/nginx/ssl/scaletrigger.crt \
  -subj "/CN=scaletrigger"

cat > /etc/nginx/sites-available/scaletrigger <<'NGINX'
server {
    listen 80;
    server_name _;

    return 301 https://$host$request_uri;
}

server {
    listen 443 ssl;
    server_name _;

    ssl_certificate /etc/nginx/ssl/scaletrigger.crt;
    ssl_certificate_key /etc/nginx/ssl/scaletrigger.key;

    location / {
        proxy_pass http://127.0.0.1:5000;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
    }
}
NGINX

rm -f /etc/nginx/sites-enabled/default
ln -sf /etc/nginx/sites-available/scaletrigger /etc/nginx/sites-enabled/scaletrigger
systemctl restart nginx
'''

var scriptWithRetryMax = replace(scriptTemplate, '__RETRY_MAX__', string(retryMaxAttempts))
var scriptWithRetryDelay = replace(scriptWithRetryMax, '__RETRY_DELAY__', string(retryDelaySeconds))
var scriptWithDotnet = replace(scriptWithRetryDelay, '__DOTNET_CHANNEL__', dotnetChannel)
var scriptWithRepo = replace(scriptWithDotnet, '__REPOSITORY_URL__', repositoryUrl)
var scriptWithAuthUser = replace(scriptWithRepo, '__AUTH_USERNAME__', authUsername)
var scriptWithAuthPassword = replace(scriptWithAuthUser, '__AUTH_PASSWORD__', authPassword)
var scriptFinal = replace(scriptWithAuthPassword, '__DB_ENV_LINES__', dbEnvLines)

output cloudInitBase64 string = base64(scriptFinal)
