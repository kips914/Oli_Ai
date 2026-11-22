# Telegram relay для OLI (обход блокировки в РФ)

С конца 2025 года Telegram в России фильтруется на уровне операторов (DPI).
Локальный бот OLI при этом не может достучаться до `api.telegram.org`, хотя
сам OLI работает. Решение — маленький релей на Cloudflare: запросы идут на
домен Cloudflare (он в РФ доступен), а Cloudflare уже сам обращается к Telegram.

## Вариант 1. Cloudflare Worker (бесплатно, 5 минут)

1. Зайдите на https://dash.cloudflare.com → **Workers & Pages** → **Create** →
   **Worker**.
2. Скопируйте туда содержимое `worker.js` и нажмите **Deploy**.
3. Откройте **Settings → Variables and Secrets** и добавьте секрет `RELAY_SECRET`
   со случайной длинной строкой (например, результат `openssl rand -hex 32`).
4. Скопируйте адрес воркера: `https://oli-bot.<ваш-поддомен>.workers.dev`.
5. В OLI: **Telegram-бот → Способ обхода → Cloudflare-релей**, вставьте адрес.
   Если задавали `RELAY_SECRET`, вставьте то же значение в поле «Секрет релея».
6. Нажмите **Проверить** — должно появиться `OK @ваш_бот через relay`.

> `*.workers.dev` иногда тоже фильтруют по IP. Если так — привяжите к воркеру
> **свой домен** (Workers → Settings → Domains & Routes → Add custom domain).

## Вариант 2. Свой VPS (nginx)

```nginx
server {
    listen 443 ssl http2;
    server_name oli-bot.example.com;

    ssl_certificate     /etc/letsencrypt/live/oli-bot.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/oli-bot.example.com/privkey.pem;

    location ~ ^/bot(.+)/([A-Za-z]+)$ {
        proxy_pass https://api.telegram.org/bot$1/$2;
        proxy_ssl_server_name on;
        proxy_set_header Host api.telegram.org;
        client_max_body_size 64m;
    }
}
```

Адрес `https://oli-bot.example.com` вставьте в OLI как релей. VPS должен быть
за пределами РФ (или с доступом к Telegram).

## Вариант 3. Прокси

Если у вас уже есть VPN/прокси-клиент с локальным SOCKS5-портом (например,
`127.0.0.1:1080`), выберите **Прокси / SOCKS5** и укажите
`socks5://127.0.0.1:1080` (или `http://127.0.0.1:8080`).

## Вариант 4. DNS-over-HTTPS

Если блокировка только DNS (провайдер подменяет ответ для `api.telegram.org`),
включите **DNS-over-HTTPS**: OLI сам разрешит адрес через Cloudflare/Google/Яндекс
DoH и подключится напрямую. Это не поможет против DPI-фильтра по SNI — тогда
нужен релей или прокси.

## Порядок, в котором OLI пробует транспорты

```
relay → proxy → DoH-direct → direct
```

Первый успешный запрос запоминается, и дальнейшие сообщения идут через него.
В консоли OLI видно, какой транспорт выбран.
