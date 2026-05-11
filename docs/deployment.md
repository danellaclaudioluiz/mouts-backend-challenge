[← back to README](../README.md)

# Production deployment

Live endpoint: <https://mouts.danellaclaudioluiz.com.br>.

Hosting topology:

```
Internet ─HTTPS:443─▶ nginx (host) ─HTTP:8080─▶ Kestrel (container)
                                                  │
                                       ┌──────────┼──────────┐
                                       ▼          ▼          ▼
                                   Postgres 16   Redis 7    Outbox
                                   (container)  (container) dispatcher
```

TLS terminates at nginx; Kestrel inside the container speaks plain HTTP
on `127.0.0.1:8080`. The container's `ASPNETCORE_HTTPS_PORTS` is
blanked in [`docker-compose.prod.yml`](../docker-compose.prod.yml). The
forwarded-headers middleware honours `X-Forwarded-{For,Proto,Host}`
from the docker bridge (`ForwardedHeaders__KnownNetworks=172.16.0.0/12`).

## Prerequisites on the VPS

- Linux host with Docker engine + compose v2, nginx, certbot.
- Repo cloned (or rsynced) into `/opt/mouts`.
- A DNS A record pointing the chosen subdomain at the VPS IP. Verify
  with `dig <host> +short` before issuing the certificate — Let's
  Encrypt's HTTP-01 challenge needs the name to resolve.

## First-time setup

### 1. Generate secrets + `.env`

```bash
JWT=$(openssl rand -base64 48 | tr -d '\n')
PG=$(openssl rand -base64 32 | tr -d '\n=')
RED=$(openssl rand -base64 32 | tr -d '\n=')

sudo install -d -m 700 -o root -g root /opt/mouts
sudo tee /opt/mouts/.env > /dev/null <<EOF
ASPNETCORE_ENVIRONMENT=Production

POSTGRES_DB=developer_evaluation
POSTGRES_USER=mouts_app
POSTGRES_PASSWORD=${PG}

REDIS_PASSWORD=${RED}

JWT_SECRET_KEY=${JWT}
Jwt__Issuer=https://mouts.danellaclaudioluiz.com.br
Jwt__Audience=mouts-sales-api

Cors__AllowedOrigins__0=https://mouts.danellaclaudioluiz.com.br
AllowedHosts=mouts.danellaclaudioluiz.com.br

ForwardedHeaders__KnownNetworks=172.16.0.0/12

RateLimit__PermitLimit=200
RateLimit__AuthPermitLimit=5

# Mongo is profiled out in docker-compose.prod.yml — these are required
# by the base compose's :? defaults; any non-empty placeholder is fine.
MONGO_USER=unused
MONGO_PASSWORD=unused
EOF
sudo chmod 600 /opt/mouts/.env
```

### 2. nginx vhost + cert

`/etc/nginx/sites-available/mouts.conf`:

```nginx
server {
    listen 80;
    listen [::]:80;
    server_name mouts.danellaclaudioluiz.com.br;
    location /.well-known/acme-challenge/ { root /var/www/certbot; }
    location / { return 301 https://$host$request_uri; }
}
server {
    listen 443 ssl http2;
    listen [::]:443 ssl http2;
    server_name mouts.danellaclaudioluiz.com.br;

    ssl_certificate     /etc/letsencrypt/live/mouts.danellaclaudioluiz.com.br/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/mouts.danellaclaudioluiz.com.br/privkey.pem;
    include /etc/letsencrypt/options-ssl-nginx.conf;
    ssl_dhparam /etc/letsencrypt/ssl-dhparams.pem;

    add_header Strict-Transport-Security "max-age=63072000; includeSubDomains" always;
    add_header X-Content-Type-Options "nosniff" always;
    add_header X-Frame-Options "DENY" always;
    add_header Referrer-Policy "no-referrer" always;

    client_max_body_size 1m;  # matches Kestrel limit in Program.cs

    location / {
        proxy_pass http://127.0.0.1:8080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Real-IP $remote_addr;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-Host $host;
        proxy_set_header Connection "";
    }
}
```

Then:

```bash
sudo ln -s /etc/nginx/sites-available/mouts.conf /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
sudo certbot --nginx \
    -d mouts.danellaclaudioluiz.com.br \
    --email <you@example.com> --agree-tos --no-eff-email --redirect
```

certbot autodetects the nginx vhost and patches the HTTP block.

### 3. Bring up the stack

```bash
cd /opt/mouts
sudo docker compose \
    --env-file /opt/mouts/.env \
    -f docker-compose.yml -f docker-compose.prod.yml \
    build
sudo docker compose \
    --env-file /opt/mouts/.env \
    -f docker-compose.yml -f docker-compose.prod.yml \
    up -d
sudo docker compose logs -f --tail 100 ambev.developerevaluation.webapi
```

The `migrate` service runs once (applies the idempotent EF script
against Postgres), exits 0; the WebApi then boots.

## Smoke

```bash
curl -I https://mouts.danellaclaudioluiz.com.br/health
curl     https://mouts.danellaclaudioluiz.com.br/health/ready
BASE=https://mouts.danellaclaudioluiz.com.br bash scripts/smoke.sh
BASE=https://mouts.danellaclaudioluiz.com.br bash scripts/hardmode.sh
```

96 + 56 = **152 checks** must pass against the live endpoint before the
deploy is considered healthy.

## Updates

```bash
cd /opt/mouts
git pull   # or rsync from your workstation
sudo docker compose \
    --env-file /opt/mouts/.env \
    -f docker-compose.yml -f docker-compose.prod.yml \
    up -d --build
```

The migrate service re-runs against the idempotent script — no-op on
already-applied migrations.

## Rollback

```bash
sudo docker compose -f docker-compose.yml -f docker-compose.prod.yml down
# Postgres data lives in a named volume — survives compose down.
# For a clean reset, also drop the volume:
sudo docker volume ls --filter name=mouts
# sudo docker volume rm mouts_<volume>  # destructive — won't be reversed
```

Cert stays issued; if the subdomain is permanently retired,
`sudo certbot revoke -d mouts.danellaclaudioluiz.com.br` to clean up.
