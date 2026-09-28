# Automatic Plugin Updater

Automatic Plugin Updater checks installed Rust plugins against uMod and safely prepares available updates.

It scans top-level `.cs` files in `oxide/plugins`, allowing it to find loaded, unloaded, and compile-failed plugins on active or restored servers.

Version 2 has been reworked for safe staging, rate-limited requests, backups, rollback, persistent check history, and grouped Discord reports.

Current release: **2.0.0**

> Version 2 rework maintained by **Mr_Padingy**, based on the original GPL-3.0 plugin by **birthdates**.

## Key features

- Checks loaded, unloaded, and compile-failed plugin files
- Detects when restored source differs from the version Oxide still has loaded
- Excludes plugin subdirectories such as `oxide/plugins/disabled`
- Uses uMod's static asset manifests instead of website searches by default
- Uses a serialised request queue with configurable spacing and retries
- Disables automatic website polling by default
- Adds random timing jitter to opt-in routine checks
- Handles HTTP `429`, server errors, and temporary network failures
- Rejects HTML error pages, empty responses, and implausible source files
- Requires exact filenames and accepts only HTTPS downloads from uMod
- Stages updates outside the live plugin directory by default
- Supports `Ignore`, `Notify`, `Stage`, and `Install` policies
- Creates source and configuration backups before installation
- Provides manual rollback
- Groups Discord notifications and complete audit reports
- Keeps detailed per-plugin console logging disabled by default
- Migrates supported legacy configuration automatically
- Supports uMod localisation for player-facing command responses

## Safe by default

The default `Stage` policy saves validated updates outside the live plugin directory. Applying an update first backs up its source and matching configuration. Downgrades are blocked, while restricted major updates may be staged for review.

Bulk application respects the same safety controls. `apu apply *` skips ignored/private plugins, downgrades, disabled major-version changes, and unknown version changes. A reviewed forward update can be applied explicitly with `apu apply PluginName`.

## Installation

1. Place `AutomaticPluginUpdater.cs` in `oxide/plugins`.
2. Grant the permission to your administrator or admin group.
3. Review the generated configuration.
4. If using Discord, add the webhook and run `apu discordtest`.
5. Run `apu check *` for a complete fresh audit.

Permission:

```text
automaticpluginupdater.update
```

Example:

```text
oxide.grant group admin automaticpluginupdater.update
```

## Commands

| Command | Description |
| --- | --- |
| `apu` | Display the command guide. |
| `apu check *` | Check every installed top-level plugin. |
| `apu check PluginName` | Check one installed plugin. |
| `apu status` | Show queue progress and recent results. |
| `apu inventory` | Count loaded and unloaded/compile-failed plugins. |
| `apu staged` | List staged updates. |
| `apu apply PluginName` | Back up and apply one reviewed staged update. |
| `apu apply *` | Apply eligible staged updates and skip items requiring individual review. |
| `apu discard <PluginName\|*>` | Delete staged updates. |
| `apu backups PluginName` | List available backups. |
| `apu rollback PluginName` | Restore the newest backup. |
| `apu cancel <PluginName\|*>` | Cancel pending checks. |
| `apu discordtest` | Test the configured Discord webhook. |

`apu queue` is an alias for `apu status`. The legacy `update <PluginName|*>` command is also supported.

## Complete audits

`apu check *` bypasses the recent-check cache and reports every result after the run finishes. Reports over 20 plugins are divided into numbered parts. Progress is saved after every result, allowing an interrupted audit to resume after an updater or server reload.

## Update policies

| Policy | Behaviour |
| --- | --- |
| `Ignore` | Do not automatically update the plugin. |
| `Notify` | Report an update without downloading it. |
| `Stage` | Validate and save the update outside the live directory. |
| `Install` | Stage, back up, and install allowed version changes. |

`Stage` is recommended for production servers. Per-plugin policies can override the default:

```json
"Per-plugin policies": {
  "ImageLibrary": "Install",
  "NTeleportation": "Notify",
  "AdminMenu": "Ignore"
}
```

## Configuration

The generated configuration is divided into clear sections:

- Checks and Scheduling
- Update Policies
- Storage and Backups
- Network and Retry Limits
- Discord Notifications
- Logging

Staged updates use `plugin_updates` and backups use `plugin_backups` by default. Both paths are relative to the server root and must remain outside `oxide/plugins`.

Completed checks are recorded in `oxide/data/AutomaticPluginUpdater.state.json`. Automatic checks skip an unchanged plugin during the configured cooldown, while manual checks bypass it.

Automatic startup, plugin-load, and routine checks are disabled by default. This avoids
unexpected traffic to uMod and gives the server owner control over when checks run. If
routine checks are enabled, the default interval is 24 hours and each run receives up to
30 minutes of random timing jitter so multiple servers do not repeatedly check together.
Use the longest interval that suits your server and avoid frequent full audits.

Discord results link to the plugin page and release notes, which should be reviewed before applying updates.

Automatic notifications, manual single-plugin notifications, and complete manual-audit reports can be controlled separately. When Discord is enabled, automatic notifications and full-audit reports are enabled by default; manual single-plugin notifications are not.

Blocked downgrades and suspicious version changes use prominent Discord warnings rather
than ordinary update notices. Large major jumps and non-standard versions are staged for
review and excluded from bulk application unless allowed by policy.

## Paid and private plugins

The updater uses public uMod listings. Paid RustHQ/ChaosCode plugins, private plugins, and locally developed plugins may have no public result.

Add these plugins to `Ignored plugins` or assign an `Ignore` policy. The updater will not scrape authenticated stores, bypass paid downloads, or substitute a similarly named public plugin.

Public and paid products can occasionally share the same filename, title, and author.
Those values cannot prove provenance. Bulk apply therefore skips ignored plugins,
downgrades, major changes disabled by policy, and unknown version changes. Explicitly
apply a reviewed forward update with `apu apply PluginName`.

## Important limitations

- Only top-level `.cs` files in `oxide/plugins` are scanned.
- Plugin subdirectories are intentionally excluded.
- uMod is the public metadata and download source.
- Static metadata and downloads use `assets.umod.org` by default.
- The legacy uMod website search fallback is disabled by default and should only be
  enabled if uMod confirms that its use is acceptable.
- Availability and acceptable use of uMod's public website endpoints are controlled by uMod.
- Source validation cannot guarantee compatibility with a particular Rust or uMod build.
- Ordinary background queues are not saved across updater reloads. Full manual audits save
  completed results and remaining plugin names so they can resume.
- Compilation failures require manual review and, when necessary, `apu rollback`.
- Administrators should review upstream release notes and configuration changes before applying updates.

## Licence and attribution

Licensed under **GPL-3.0-or-later**.

Original plugin by **birthdates**. Version 2 rework and maintenance by **Mr_Padingy**.
