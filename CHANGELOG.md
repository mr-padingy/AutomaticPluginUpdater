# Changelog

## 2.0.0

- Replaced direct-to-live downloads with validated staging.
- Added serialised request throttling and exponential retry handling.
- Prevented HTTP 429/HTML responses from overwriting plugin source files.
- Added `Ignore`, `Notify`, `Stage`, and `Install` policies.
- Added per-plugin policies and safe version-change controls.
- Added backups, retention, apply, discard, listing, and rollback commands.
- Added queue status, cancellation, and requester progress messages.
- Added HTTPS/uMod-host enforcement and storage-path safety checks.
- Added grouped Discord settings and a webhook test command.
- Added configurable Discord events for available, staged, installed, and failed updates.
- Grouped Discord events into rate-conscious summary embeds with plugin, author, result, and uMod links.
- Made manual `apu check *` runs complete tracked audits, including up-to-date, ignored,
  unloaded, and failed plugin results, with safely numbered multi-part Discord reports.
- Persisted unfinished full audits so updater or server reloads resume the remaining checks.
- Reduced console noise by suppressing per-plugin full-audit chatter and summarising cache skips.
- Disabled detailed per-plugin queue logging by default for new configurations.
- Moved player-facing command and status responses into the uMod language system.
- Corrected the startup-check configuration label while preserving the earlier v2 key during migration.
- Added separate Discord switches for automatic checks, manual single-plugin checks, and complete manual audits.
- Prevented apply and rollback reloads from immediately rechecking plugins changed by the updater itself.
- Made the installed source file authoritative when its `[Info]` version differs from stale loaded-plugin metadata.
- Prevented bulk apply from bypassing ignored/private-plugin, downgrade, major-version, and unknown-version safeguards.
- Added prominent Discord safety events for downgrades, possible identity collisions, large major jumps, and unknown version changes.
- Added Discord delivery confirmations and direct release-notes links for discovered updates.
- Added automatic migration from legacy and early-v2 configuration formats.
- Added persistent per-plugin check history so reloads resume without repeating recently completed work.
- Disabled automatic website polling by default, changed the opt-in routine interval to 24 hours, and added random timing jitter.
- Replaced normal website searches with uMod's static asset manifests and disabled the legacy website search fallback by default.
- Reported missing static manifests as unpublished, removed, or private plugins instead of network failures.
- Added filesystem inventory and update support for unloaded and compile-failed plugins.
- Excluded Oxide core/interface plugins and plugin subdirectories from update scans.
- Added source metadata parsing for legacy and extended `[Info]` attribute formats.
- Added paired plugin configuration snapshots during apply and config restoration during rollback.
- Fixed author-filter construction and case-sensitive matching issues.
- Fixed inverted Discord response validation from v1.

Original plugin by birthdates. Version 2 rework maintained by Mr_Padingy.
