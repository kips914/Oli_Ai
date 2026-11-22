# OLI Mobile — skeleton

The mobile client is reserved here for the future Android/iOS app. PC-side transport is `OLI.MobileGateway`.

Flow: `Phone -> authenticated Gateway -> OLI Runtime -> llama.cpp -> Gateway -> Phone`.

The gateway currently binds to `127.0.0.1` only. Do not expose it directly to the Internet. A future relay will provide TLS, device authentication, QR pairing, streaming responses and online/offline state.

Planned mobile screens: Chat, QR pairing, Fast/Performance mode, photo/vision mode, connection status and settings.
