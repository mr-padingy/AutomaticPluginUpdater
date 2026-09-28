# Automatic Plugin Updater 2.0 test guide

Test release candidates on a non-production server and keep
your existing `AutomaticPluginUpdater.cs` somewhere outside the Oxide plugin
directory before installing it.

## Expected first-run behaviour

- The default update policy is `Stage`; downloads do not replace live plugins.
- Staged files are written to `plugin_updates` under the server root.
- Backups made during apply/install are written to `plugin_backups` under the server root.
- Search and download requests are serialised, eight seconds apart by default.
- HTTP 429, timeout, and server errors retry with exponential backoff.
- HTML/error responses and implausible C# files are rejected before disk staging.

Existing v1.5.5 and earlier v2 configuration files are migrated to the grouped
v2 format when the configuration is saved.

## Commands

Run these from the server console or with a player granted
`automaticpluginupdater.update`:

```text
apu check PluginName
apu check *
apu status
apu queue
apu inventory
apu apply PluginName
apu apply *
apu discordtest
apu staged
apu discard PluginName
apu discard *
apu backups PluginName
apu rollback PluginName
apu cancel PluginName
apu cancel *
```

The legacy command remains available:

```text
update PluginName
update *
```

## Suggested smoke test

1. Load the updater and confirm that it compiles without errors.
2. Inspect the regenerated config. Leave the default update policy set to `Stage`.
3. Run `apu check PluginName` for one known uMod plugin.
4. Use `apu status` while it runs.
5. Confirm an available update appears in `plugin_updates` and that the
   currently loaded plugin file was not modified.
6. Open the staged file and confirm it is C# rather than HTML.
7. Run `apu apply PluginName`.
8. Confirm a timestamped backup exists in `plugin_backups` and the updated
   plugin recompiles.
9. Run `apu check *` and verify checks occur gradually rather than in a burst.
10. Confirm the final Discord audit includes every queued plugin and is divided into
    clearly numbered parts when more than 20 plugins are checked.
11. During a separate full audit, reload the updater and confirm the unfinished audit
    resumes from saved state before producing its final Discord report.
12. Run `apu discordtest` and confirm both the command response and HTTP delivery result.

## Useful policy examples

```json
"Update Policies": {
  "Default policy (Ignore, Notify, Stage, Install)": "Stage",
  "Per-plugin policies": {
    "ImageLibrary": "Install",
    "NTeleportation": "Notify",
    "MyPrivatePlugin": "Ignore"
  }
}
```

Use `Install` only after staging and apply have been proven on the test server.

## Please capture

For any failure, retain the complete compile error or console message, your
generated configuration, Oxide/uMod version, Rust server version, and the name
of the plugin being checked. Do not include a Discord webhook URL in reports.
