# Jellyfin 12.4 compatibility port (development only)

This branch retargets the SSO Authentication plugin to Jellyfin 12.4 and .NET 10. The plugin version is 4.2.0.0; the established GUID remains `505ce9d1-d916-42fa-86ca-673ef241d7df`. Do not publish to the 4.1.0.0 repository catalog or deploy to production until all tests pass on a Jellyfin 12.4 development server.

## Compatibility gates

1. Compile all projects on .NET 10 with Jellyfin.Controller/Model 12.4.0; resolve any breaking changes before producing a ZIP.
2. Verify native Jellyfin authentication token exchange and the unchanged issuer/subject identity links, including newly created accounts and existing migrated users.
3. Verify user-library authorization policy persistence and lack of privilege escalation after a restart.
4. Verify config save, administrative linking endpoints, and preservation of original user GUIDs, OIDC keys, and settings.
5. Check Firefox, Chrome and Android clients using the latest Jellyfin web UI; confirm callback exchange and retry/logout behavior.
6. Upgrade only the disposable development Jellyfin instance with a complete backup and test rollback. Production remains on the proven 10.11/4.1 combination.

The standalone migration CLI is retargeted to .NET 10 only to keep the solution buildable; no account re-migration is expected on upgrade. The stable 4.1.0.0 release/tag/catalog must remain untouched.
