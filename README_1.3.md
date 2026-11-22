# OLI 1.3.1_released

## Implemented
- Fast / Performance generation modes.
- Persistent user profile memory: name, age, interests, skills, projects.
- Date/time injected into the system environment at request time.
- Settings GUI: themes, mode, context/threads/GPU-layer profiles, cache reset, engine restart, diagnostics, full user-data reset.
- Persistent settings in `data/settings.json`.
- Mobile architecture skeleton: `OLI.MobileGateway` + `Mobile/README.md`.

## Mobile security baseline
The gateway is localhost-only by default. Do not port-forward it. Internet access will be implemented through an authenticated TLS relay in a later version.

## Memory separation
User profile/memory is kept outside model weights. This follows the OLI architecture principle: mutable memory and context must not be confused with training/fine-tuning.
