// Automatic Plugin Updater
// Originally created by birthdates. Version 2 rework maintained by Mr_Padingy (2026).
// SPDX-License-Identifier: GPL-3.0-or-later

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;

namespace Oxide.Plugins
{
    [Info("Automatic Plugin Updater", "birthdates / Mr_Padingy", "2.0.0")]
    [Description("Safely checks, stages, and installs updates for uMod plugins.")]
    public class AutomaticPluginUpdater : CovalencePlugin
    {
        private const string ManifestPluginUrl = "https://assets.umod.org/plugins/{0}.json";
        private const string LegacySearchUrl = "https://umod.org/plugins/search.json?query={0}&page=1&sort=title&sortdir=asc";
        private const string Permission = "automaticpluginupdater.update";
        private const string ChatPrefix = "[Automatic Plugin Updater] ";
        private readonly Queue<Job> _queue = new Queue<Job>();
        private readonly HashSet<string> _queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _results = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<DiscordNotice> _discordBatch = new List<DiscordNotice>();
        private readonly Dictionary<int, AuditSession> _audits = new Dictionary<int, AuditSession>();
        private readonly HashSet<string> _selfReloads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly System.Random _random = new System.Random();
        private int _nextAuditId;
        private PersistentState _state = new PersistentState();
        private ConfigData _config;
        private Timer _queueTimer, _routineTimer;
        private bool _ready, _busy;
        private DateTime _lastRequest = DateTime.MinValue;
        private string _active = "none";
        private int _lastQueuedLoaded, _lastQueuedUnloaded, _lastMetadataFailures, _lastRecentSkips;

        [UsedImplicitly]
        private void Init()
        {
            permission.RegisterPermission(Permission, this);
            AddCovalenceCommand("update", nameof(LegacyCommand), Permission);
            AddCovalenceCommand("apu", nameof(ApuCommand), Permission);
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["LegacyUsage"] = "Usage: /update <*|plugin>",
                ["CheckAllUsage"] = "Check all plugins: `apu check *`",
                ["CheckOneUsage"] = "Check an individual plugin: `apu check PluginName`",
                ["Status"] = "Active: {0}; queued: {1}; completed: {2}",
                ["HelpHeader"] = "Automatic Plugin Updater commands:",
                ["HelpCheckAll"] = "  apu check *                 Check every installed plugin",
                ["HelpCheckOne"] = "  apu check PluginName        Check one plugin",
                ["HelpStatus"] = "  apu status                  Show queue progress and recent results",
                ["HelpInventory"] = "  apu inventory               List loaded and unloaded plugin files",
                ["HelpStaged"] = "  apu staged                  List updates waiting in staging",
                ["HelpApply"] = "  apu apply <PluginName|*>    Install one or all staged updates",
                ["HelpDiscard"] = "  apu discard <PluginName|*>  Delete one or all staged updates",
                ["HelpBackups"] = "  apu backups PluginName      List available backups",
                ["HelpRollback"] = "  apu rollback PluginName     Restore the newest backup",
                ["HelpCancel"] = "  apu cancel <PluginName|*>   Cancel queued checks",
                ["HelpDiscord"] = "  apu discordtest             Test the configured Discord webhook",
                ["CheckRunning"] = "A check is already running. Wait for it to finish, or use `apu cancel *`, before starting a complete fresh audit.",
                ["QueuedAll"] = "Queued {0} installed plugin checks: {1} loaded, {2} unloaded/compile-failed.{3}",
                ["MetadataSkipped"] = " Skipped {0} file(s) with unreadable [Info] metadata.",
                ["PluginNotFound"] = "No installed top-level plugin named '{0}'.",
                ["QueuedOne"] = "Queued {0}.",
                ["QueuedOrIgnored"] = "{0} is queued or ignored.",
                ["AppliedCount"] = "Applied {0} staged update(s).",
                ["BulkApplySummary"] = "Applied {0} staged update(s); skipped {1} requiring individual review.",
                ["DiscardedCount"] = "Discarded {0} staged update(s).",
                ["NoStaged"] = "No staged updates.",
                ["StagedList"] = "Staged updates ({0}): {1}",
                ["DiscardFailed"] = "Could not discard {0}: {1}",
                ["ApplyFailed"] = "Apply failed: {0}",
                ["RawLine"] = "{0}",
                ["NoBackups"] = "No backups for {0}.",
                ["BackupsHeader"] = "Backups for {0} (newest first):",
                ["NoBackup"] = "No backup exists for {0}.",
                ["RollbackFailed"] = "Rollback failed: {0}",
                ["ConfigRestored"] = "Restored the matching configuration snapshot.",
                ["ConfigRestoreFailed"] = "Source was rolled back, but config restore failed: {0}",
                ["RolledBack"] = "Rolled back {0} using {1}.",
                ["Cancelled"] = "Cancelled {0} queued check(s).{1}",
                ["ActiveNotCancelled"] = " The active request cannot be cancelled.",
                ["NothingQueued"] = "No new plugin checks were queued; an existing check may already be running.",
                ["Inventory"] = "Installed C# files: {0}; loaded: {1}; unloaded/compile-failed: {2}.",
                ["UnavailableList"] = "Unloaded/compile-failed: {0}{1}",
                ["Checking"] = "Checking {0} v{1} ({2}, {3}).",
                ["InstallPolicyBlocked"] = "{0} v{1} is a {2} update. Automatic installation of {2} updates is disabled in config, so it will be staged instead.",
                ["Retrying"] = "{0}; retrying in {1} seconds ({2}/{3}).",
                ["PluginResult"] = "{0}: {1}.",
                ["AuditCompleteDiscord"] = "Full check complete: {0} plugin result(s). Sending the complete Discord audit summary.",
                ["AuditCompleteNoDiscord"] = "Full check complete: {0} plugin result(s). Discord reporting is disabled.",
                ["DiscordDisabled"] = "Discord webhooks are disabled in the configuration.",
                ["DiscordUrlMissing"] = "No Discord webhook URL is configured.",
                ["DiscordTesting"] = "Sending a Discord webhook test...",
                ["DiscordTestSucceeded"] = "Discord webhook test succeeded (HTTP {0}).",
                ["DiscordTestFailed"] = "Discord webhook test failed (HTTP {0}). Check the server console."
            }, this);
        }

        [UsedImplicitly]
        private void OnServerInitialized()
        {
            _ready = true;
            LoadState();
            EnsureDirectories();
            var resumedAudit = ResumeAudit();
            if (!resumedAudit && _config.CheckOnStart && !_config.DisableStartupCheck) QueueAll(null);
            ScheduleRoutineCheck();
        }

        private void ScheduleRoutineCheck()
        {
            if (!_config.RoutineChecks) return;
            var intervalSeconds = Math.Max(60f, _config.RoutineMinutes * 60f);
            var maximumJitter = Math.Min(intervalSeconds * 0.1f, 1800f);
            var delaySeconds = intervalSeconds + (float)(_random.NextDouble() * maximumJitter);
            _routineTimer = timer.Once(delaySeconds, () =>
            {
                QueueAll(null);
                ScheduleRoutineCheck();
            });
        }

        [UsedImplicitly]
        private void Unload()
        {
            if (_queueTimer != null) _queueTimer.Destroy();
            if (_routineTimer != null) _routineTimer.Destroy();
        }

        [UsedImplicitly]
        private void OnPluginLoaded(Plugin plugin)
        {
            if (plugin != null && _selfReloads.Remove(plugin.Name)) return;
            if (_ready && _config.CheckNewPlugins) Enqueue(plugin);
        }

        [HookMethod("CheckActivePlugins")]
        private void CheckActivePlugins() { QueueAll(null); }

        [HookMethod("CheckPlugin")]
        private void CheckPlugin(Plugin plugin, bool bypass = false) { Enqueue(plugin, null, bypass); }

        private bool LegacyCommand(IPlayer caller, string command, IList<string> args)
        {
            if (args.Count == 0) { Reply(caller, "LegacyUsage"); return false; }
            return QueueTarget(caller, args[0]);
        }

        private bool ApuCommand(IPlayer caller, string command, IList<string> args)
        {
            if (args.Count == 0) { SendUsage(caller); return false; }
            switch (args[0].ToLowerInvariant())
            {
                case "check":
                    if (args.Count < 2)
                    {
                        Reply(caller, "CheckAllUsage");
                        Reply(caller, "CheckOneUsage");
                        return false;
                    }
                    return QueueTarget(caller, args[1]);
                case "status":
                case "queue":
                    Reply(caller, "Status", _active, _queue.Count, _results.Count);
                    foreach (var item in _results.Take(10)) Reply(caller, "PluginResult", item.Key, item.Value);
                    return true;
                case "apply":
                    if (args.Count < 2) { SendUsage(caller); return false; }
                    return ApplyTarget(caller, args[1]);
                case "discordtest":
                    SendDiscordTest(caller);
                    return true;
                case "staged":
                    ListStaged(caller);
                    return true;
                case "discard":
                    if (args.Count < 2) { SendUsage(caller); return false; }
                    return DiscardTarget(caller, args[1]);
                case "backups":
                    if (args.Count < 2) { SendUsage(caller); return false; }
                    ListBackups(caller, args[1]);
                    return true;
                case "rollback":
                    if (args.Count < 2) { SendUsage(caller); return false; }
                    return RollbackTarget(caller, args[1]);
                case "cancel":
                    if (args.Count < 2) { SendUsage(caller); return false; }
                    return CancelTarget(caller, args[1]);
                case "inventory":
                    SendInventory(caller);
                    return true;
                default: SendUsage(caller); return false;
            }
        }

        private void SendUsage(IPlayer caller)
        {
            Reply(caller, "HelpHeader");
            Reply(caller, "HelpCheckAll");
            Reply(caller, "HelpCheckOne");
            Reply(caller, "HelpStatus");
            Reply(caller, "HelpInventory");
            Reply(caller, "HelpStaged");
            Reply(caller, "HelpApply");
            Reply(caller, "HelpDiscard");
            Reply(caller, "HelpBackups");
            Reply(caller, "HelpRollback");
            Reply(caller, "HelpCancel");
            Reply(caller, "HelpDiscord");
        }
        private void Reply(IPlayer caller, string key, params object[] args)
        {
            if (caller == null) return;
            var message = lang.GetMessage(key, this, caller.Id);
            caller.Message(ChatPrefix + (args.Length == 0 ? message : string.Format(message, args)));
        }

        private bool QueueTarget(IPlayer caller, string target)
        {
            if (target == "*")
            {
                if (_busy || _queue.Count > 0)
                {
                    Reply(caller, "CheckRunning");
                    return false;
                }
                var count = QueueAll(caller);
                var skipped = _lastMetadataFailures > 0 ? string.Format(lang.GetMessage("MetadataSkipped", this, caller.Id), _lastMetadataFailures) : "";
                Reply(caller, "QueuedAll", count, _lastQueuedLoaded, _lastQueuedUnloaded, skipped);
                return true;
            }
            PluginTarget plugin;
            if (!TryFindTarget(target, out plugin)) { Reply(caller, "PluginNotFound", target); return false; }
            Reply(caller, EnqueueTarget(plugin, caller, true) ? "QueuedOne" : "QueuedOrIgnored", plugin.Name);
            return true;
        }

        private bool ApplyTarget(IPlayer caller, string target)
        {
            var bulk = target == "*";
            var selected = target == "*" ? InstalledTargets() : InstalledTargets().Where(x =>
                string.Equals(x.Name, target, StringComparison.OrdinalIgnoreCase) || string.Equals(x.Title, target, StringComparison.OrdinalIgnoreCase));
            var count = 0; var skipped = 0;
            foreach (var plugin in selected)
            {
                string error;
                if (Apply(plugin, out error, bulk)) count++;
                else if (bulk)
                {
                    if (File.Exists(StagedPath(plugin.Name))) { skipped++; PrintWarning("Bulk apply skipped {0}: {1}", plugin.Name, error); }
                }
                else Reply(caller, "ApplyFailed", error);
            }
            if (bulk) Reply(caller, "BulkApplySummary", count, skipped);
            else Reply(caller, "AppliedCount", count);
            return count > 0;
        }

        private void ListStaged(IPlayer caller)
        {
            var files = Directory.Exists(StagingDirectory) ? Directory.GetFiles(StagingDirectory, "*.cs") : new string[0];
            if (files.Length == 0) { Reply(caller, "NoStaged"); return; }
            Reply(caller, "StagedList", files.Length, string.Join(", ", files.Select(Path.GetFileNameWithoutExtension).ToArray()));
        }

        private bool DiscardTarget(IPlayer caller, string target)
        {
            PluginTarget plugin = null;
            if (target != "*" && !TryFindTarget(target, out plugin)) { Reply(caller, "PluginNotFound", target); return false; }
            var files = target == "*" ? (Directory.Exists(StagingDirectory) ? Directory.GetFiles(StagingDirectory, "*.cs") : new string[0]) : new[] { StagedPath(plugin.Name) };
            var removed = 0;
            foreach (var file in files) { try { if (File.Exists(file)) { File.Delete(file); removed++; } } catch (Exception ex) { Reply(caller, "DiscardFailed", Path.GetFileName(file), ex.Message); } }
            Reply(caller, "DiscardedCount", removed);
            return removed > 0;
        }

        private void ListBackups(IPlayer caller, string target)
        {
            PluginTarget plugin;
            if (!TryFindTarget(target, out plugin)) { Reply(caller, "PluginNotFound", target); return; }
            var files = BackupFiles(plugin.Name);
            if (files.Length == 0) { Reply(caller, "NoBackups", plugin.Name); return; }
            Reply(caller, "BackupsHeader", plugin.Name);
            foreach (var file in files.Take(10)) Reply(caller, "RawLine", Path.GetFileName(file));
        }

        private bool RollbackTarget(IPlayer caller, string target)
        {
            PluginTarget plugin;
            if (!TryFindTarget(target, out plugin)) { Reply(caller, "PluginNotFound", target); return false; }
            var backups = BackupFiles(plugin.Name);
            if (backups.Length == 0) { Reply(caller, "NoBackup", plugin.Name); return false; }
            string error;
            if (!ReplaceLiveFile(plugin, backups[0], "pre-rollback", out error)) { Reply(caller, "RollbackFailed", error); return false; }
            var configSnapshot = Path.ChangeExtension(backups[0], ".config.json");
            if (File.Exists(configSnapshot))
            {
                try { File.Copy(configSnapshot, PluginConfigPath(plugin.Name), true); Reply(caller, "ConfigRestored"); }
                catch (Exception ex) { Reply(caller, "ConfigRestoreFailed", ex.Message); }
            }
            Reply(caller, "RolledBack", plugin.Name, Path.GetFileName(backups[0]));
            return true;
        }

        private bool CancelTarget(IPlayer caller, string target)
        {
            var kept = new Queue<Job>(); var removed = 0;
            while (_queue.Count > 0)
            {
                var job = _queue.Dequeue();
                if (target == "*" || string.Equals(job.Target.Name, target, StringComparison.OrdinalIgnoreCase))
                {
                    _queued.Remove(job.Target.Name);
                    removed++;
                    RecordAuditResult(job, "Cancelled by administrator");
                }
                else kept.Enqueue(job);
            }
            while (kept.Count > 0) _queue.Enqueue(kept.Dequeue());
            var activeMessage = _active != "none" ? lang.GetMessage("ActiveNotCancelled", this, caller.Id) : "";
            Reply(caller, "Cancelled", removed, activeMessage);
            return removed > 0;
        }

        private int QueueAll(IPlayer caller)
        {
            var count = 0; _lastQueuedLoaded = _lastQueuedUnloaded = _lastMetadataFailures = _lastRecentSkips = 0;
            AuditSession audit = null;
            if (caller != null)
            {
                audit = new AuditSession { Id = ++_nextAuditId, Caller = caller, StartedUtc = DateTime.UtcNow };
                _audits[audit.Id] = audit;
            }
            foreach (var plugin in InstalledTargets())
            {
                if (EnqueueTarget(plugin, caller, caller != null, audit == null ? 0 : audit.Id)) { count++; if (plugin.IsLoaded) _lastQueuedLoaded++; else _lastQueuedUnloaded++; }
            }
            if (audit != null)
            {
                audit.Expected = count;
                if (count == 0) { _audits.Remove(audit.Id); Reply(caller, "NothingQueued"); }
                else
                {
                    _state.Audit = new PersistentAudit
                    {
                        StartedUtc = audit.StartedUtc,
                        PendingPlugins = _queue.Where(x => x.AuditId == audit.Id).Select(x => x.Target.Name).ToList(),
                        Results = new List<AuditResult>()
                    };
                    SaveState();
                }
            }
            else if (_config.Verbose && _lastRecentSkips > 0)
                Puts("Automatic scan queued {0} plugin(s); skipped {1} recently checked plugin(s).", count, _lastRecentSkips);
            return count;
        }

        private void SendInventory(IPlayer caller)
        {
            var directory = LivePluginDirectory;
            var installed = Directory.Exists(directory) ? Directory.GetFiles(directory, "*.cs", SearchOption.TopDirectoryOnly) : new string[0];
            var loaded = new HashSet<string>(plugins.GetAll().Where(IsPhysicalPlugin).Select(x => Path.GetFileName(PluginFilePath(x))), StringComparer.OrdinalIgnoreCase);
            var unavailable = installed.Where(x => !loaded.Contains(Path.GetFileName(x))).Select(Path.GetFileNameWithoutExtension).OrderBy(x => x).ToArray();
            Reply(caller, "Inventory", installed.Length, loaded.Count, unavailable.Length);
            if (unavailable.Length > 0) Reply(caller, "UnavailableList", string.Join(", ", unavailable.Take(20).ToArray()), unavailable.Length > 20 ? " ..." : "");
        }

        private IEnumerable<PluginTarget> InstalledTargets()
        {
            var loadedByPath = plugins.GetAll().Where(IsPhysicalPlugin).ToDictionary(x => PluginFilePath(x), x => x, StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(LivePluginDirectory)) yield break;
            foreach (var file in Directory.GetFiles(LivePluginDirectory, "*.cs", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetFullPath(file), Path.GetFullPath(Filename), StringComparison.OrdinalIgnoreCase)) continue;
                PluginTarget parsed;
                Plugin loaded;
                var hasLoaded = loadedByPath.TryGetValue(Path.GetFullPath(file), out loaded);
                if (TryParseTarget(file, out parsed))
                {
                    if (hasLoaded)
                    {
                        parsed.Plugin = loaded;
                        parsed.LoadedVersion = loaded.Version.ToString();
                    }
                    yield return parsed;
                }
                else if (hasLoaded) yield return TargetFromLoaded(loaded);
                else { _lastMetadataFailures++; PrintWarning("Skipping {0}; could not read a valid [Info] attribute.", Path.GetFileName(file)); }
            }
        }

        private bool TryFindTarget(string name, out PluginTarget target)
        {
            target = InstalledTargets().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Title, name, StringComparison.OrdinalIgnoreCase));
            return target != null;
        }

        private PluginTarget TargetFromLoaded(Plugin plugin)
        {
            return new PluginTarget { Plugin = plugin, Name = plugin.Name, Title = plugin.Title, Author = plugin.Author,
                Version = plugin.Version.ToString(), LoadedVersion = plugin.Version.ToString(), FilePath = PluginFilePath(plugin) };
        }

        private bool TryParseTarget(string file, out PluginTarget target)
        {
            target = null;
            try
            {
                return TryParseTargetSource(File.ReadAllText(file), file, out target);
            }
            catch { return false; }
        }

        private bool TryParseTargetSource(string source, string file, out PluginTarget target)
        {
            target = null;
            if (string.IsNullOrWhiteSpace(source)) return false;
            var match = Regex.Match(source, @"\[Info\s*\(\s*\""(?<title>[^\""\r\n]+)\""\s*,\s*\""(?<author>[^\""\r\n]*)\""\s*,\s*\""(?<version>[^\""\r\n]+)\""", RegexOptions.IgnoreCase);
            if (!match.Success) return false;
            target = new PluginTarget { Name = Path.GetFileNameWithoutExtension(file), Title = match.Groups["title"].Value,
                Author = match.Groups["author"].Value, Version = match.Groups["version"].Value, FilePath = Path.GetFullPath(file) };
            return true;
        }

        private bool Enqueue(Plugin plugin, IPlayer caller = null, bool force = false)
        {
            if (!_ready || plugin == null || plugin.Equals(this) || !IsPhysicalPlugin(plugin)) return false;
            return EnqueueTarget(TargetFromLoaded(plugin), caller, force);
        }

        private bool EnqueueTarget(PluginTarget plugin, IPlayer caller = null, bool force = false, int auditId = 0)
        {
            if (!_ready || plugin == null || string.Equals(Path.GetFullPath(plugin.FilePath), Path.GetFullPath(Filename), StringComparison.OrdinalIgnoreCase)) return false;
            CheckState recent;
            if (!force && _state.Plugins.TryGetValue(plugin.Name, out recent) &&
                string.Equals(recent.LocalVersion, plugin.Version, StringComparison.OrdinalIgnoreCase) &&
                DateTime.UtcNow - recent.CheckedUtc < TimeSpan.FromMinutes(_config.MinimumRecheckMinutes))
            {
                _lastRecentSkips++;
                return false;
            }
            var policy = PolicyFor(plugin);
            if (policy == UpdatePolicy.Ignore && !force) return false;
            if (!_queued.Add(plugin.Name)) return false;
            _queue.Enqueue(new Job { Target = plugin, Policy = policy, Caller = caller, AuditId = auditId });
            Schedule(0.1f);
            return true;
        }

        private void Schedule(float delay)
        {
            if (_busy || _queueTimer != null || _queue.Count == 0) return;
            var throttle = Math.Max(0d, _config.RequestSeconds - (DateTime.UtcNow - _lastRequest).TotalSeconds);
            _queueTimer = timer.Once((float)Math.Max(0.1d, Math.Max(delay, throttle)), () => { _queueTimer = null; ProcessNext(); });
        }

        private void ProcessNext()
        {
            if (_busy || _queue.Count == 0) return;
            var job = _queue.Dequeue();
            if (job.Target == null || !File.Exists(job.Target.FilePath)) { Fail(job, "Plugin file disappeared before check"); return; }
            StartSearch(job);
        }

        private void StartSearch(Job job)
        {
            _busy = true; _active = job.Target.Name; _lastRequest = DateTime.UtcNow;
            var query = job.SearchAttempt == 0 && !string.IsNullOrEmpty(job.Target.Title) ? job.Target.Title : job.Target.Name;
            var url = job.UseLegacySearch
                ? string.Format(LegacySearchUrl, Uri.EscapeDataString(query))
                : string.Format(ManifestPluginUrl, Uri.EscapeDataString(job.Target.Name));
            if (job.Target.HasVersionMismatch && !job.VersionMismatchReported)
            {
                job.VersionMismatchReported = true;
                PrintWarning("{0}: file is v{1}, but Oxide still has v{2} loaded. Using the file version for this check.", job.Target.Name, job.Target.Version, job.Target.LoadedVersion);
            }
            if (job.AuditId == 0) Reply(job.Caller, "Checking", job.Target.Name, job.Target.Version, job.Target.IsLoaded ? "loaded" : "unloaded", job.Policy);
            if (_config.Verbose && job.Caller == null) Puts("Checking {0} v{1} ({2}, {3}, source: {4}).", job.Target.Name, job.Target.Version, job.Target.IsLoaded ? "loaded" : "unloaded", job.Policy, job.UseLegacySearch ? "legacy search" : "static manifest");
            webrequest.Enqueue(url, "", (code, body) => SearchComplete(job, code, body), this, RequestMethod.GET);
        }

        private void SearchComplete(Job job, int code, string body)
        {
            _busy = false;
            var sourceName = job.UseLegacySearch ? "Legacy search" : "Manifest lookup";
            if (Retryable(code)) { Retry(job, sourceName + " HTTP " + code); return; }
            if (code != 200)
            {
                if (!job.UseLegacySearch && _config.AllowLegacySearchFallback)
                {
                    job.UseLegacySearch = true;
                    job.Attempts = 0;
                    StartSearch(job);
                    return;
                }
                Complete(job, sourceName + " failed: HTTP " + code);
                return;
            }
            SearchResult found; string error;
            var foundResult = job.UseLegacySearch
                ? FindResult(job.Target, body, out found, out error)
                : FindManifestResult(job.Target, body, out found, out error);
            if (!foundResult)
            {
                if (!job.UseLegacySearch && _config.AllowLegacySearchFallback)
                {
                    job.UseLegacySearch = true;
                    job.Attempts = 0;
                    StartSearch(job);
                    return;
                }
                if (job.UseLegacySearch && job.SearchAttempt == 0 && !string.Equals(job.Target.Title, job.Target.Name, StringComparison.OrdinalIgnoreCase))
                {
                    job.SearchAttempt = 1; _busy = true;
                    _queueTimer = timer.Once(Math.Max(0.1f, _config.RequestSeconds), () => { _queueTimer = null; StartSearch(job); });
                    return;
                }
                Complete(job, error); return;
            }
            if (SameVersion(found.Version, job.Target.Version)) { Complete(job, "Up to date at v" + job.Target.Version); return; }
            var type = UpdateKind(found.Version, job.Target.Version);
            bool allowed;
            var installationAllowed = _config.VersionSettings.TryGetValue(type, out allowed) && allowed;
            if (type == UpdateType.Backward)
            {
                job.Result = found;
                var warning = "Remote v" + found.Version + " is older than local v" + job.Target.Version + ". Downgrade blocked; this may indicate a public/private plugin identity collision.";
                Notify(job, warning, DiscordEvent.Blocked);
                Complete(job, "[Safety] " + warning);
                return;
            }
            job.Advisory = VersionAdvisory(job.Target.Version, found.Version, type);
            if (job.Policy == UpdatePolicy.Ignore)
            { job.Result = found; Complete(job, "v" + found.Version + " available; ignored by configured policy"); return; }
            if (job.Policy == UpdatePolicy.Notify)
            { job.Result = found; Notify(job, "v" + found.Version + " is available.", DiscordEvent.Available); Complete(job, "v" + found.Version + " available; notify only"); return; }
            if (job.Policy == UpdatePolicy.Install && !installationAllowed)
            {
                job.Policy = UpdatePolicy.Stage;
                job.InstallBlockedByVersionPolicy = true;
                job.BlockedUpdateType = type;
                Reply(job.Caller, "InstallPolicyBlocked", job.Target.Name, found.Version, type);
            }
            job.Result = found; job.Attempts = 0;
            _busy = true;
            _queueTimer = timer.Once(Math.Max(0.1f, _config.RequestSeconds), () =>
            {
                _queueTimer = null;
                Download(job);
            });
        }

        private void Download(Job job)
        {
            _busy = true; _active = job.Target.Name; _lastRequest = DateTime.UtcNow;
            webrequest.Enqueue(job.Result.Url, "", (code, body) => DownloadComplete(job, code, body), this, RequestMethod.GET);
        }

        private void DownloadComplete(Job job, int code, string body)
        {
            _busy = false;
            if (Retryable(code)) { Retry(job, "Download HTTP " + code); return; }
            if (code != 200) { Fail(job, "Download failed: HTTP " + code + "; live file untouched"); return; }
            string error;
            if (!ValidSource(job.Target, body, out error)) { Fail(job, "Rejected download: " + error); return; }
            if (!Stage(job.Target, body, out error)) { Fail(job, "Staging failed: " + error); return; }
            if (job.Policy == UpdatePolicy.Install && !Apply(job.Target, out error))
            { Fail(job, "Staged v" + job.Result.Version + ", install failed: " + error); return; }
            var result = (job.Policy == UpdatePolicy.Install ? "Installed" : "Staged") + " v" + job.Result.Version;
            if (job.InstallBlockedByVersionPolicy) result += " (automatic installation of " + job.BlockedUpdateType + " updates is disabled in config)";
            if (!string.IsNullOrWhiteSpace(job.Advisory)) result += " (review required: " + job.Advisory + ")";
            Notify(job, result + ".", job.Policy == UpdatePolicy.Install ? DiscordEvent.Installed : DiscordEvent.Staged);
            Complete(job, result);
        }

        private void Retry(Job job, string reason)
        {
            job.Attempts++;
            if (job.Attempts > _config.MaxRetries) { Fail(job, reason + "; retry limit reached, live file untouched"); return; }
            var delay = (float)Math.Min(_config.MaxRetrySeconds, _config.InitialRetrySeconds * Math.Pow(2, job.Attempts - 1));
            PrintWarning("{0}; retrying {1} in {2:0}s ({3}/{4}).", reason, job.Target.Name, delay, job.Attempts, _config.MaxRetries);
            Reply(job.Caller, "Retrying", reason, delay.ToString("0"), job.Attempts, _config.MaxRetries);
            _queue.Enqueue(job); _active = "none"; Schedule(delay);
        }

        private void Complete(Job job, string result, bool remember = true)
        {
            var name = job.Target == null ? "Unknown plugin" : job.Target.Name;
            _busy = false; _active = "none"; _queued.Remove(name); _results[name] = result;
            if (remember && job.Target != null)
            {
                _state.Plugins[name] = new CheckState { CheckedUtc = DateTime.UtcNow, LocalVersion = job.Target.Version, Result = result };
                SaveState();
            }
            if (_config.Verbose && job.Caller == null && !job.ErrorReported) Puts("{0}: {1}", name, result);
            if (job.AuditId == 0) Reply(job.Caller, "PluginResult", name, result);
            RecordAuditResult(job, result);
            Schedule(0.1f);
            if (_queue.Count == 0 && !_busy && _queueTimer == null && _audits.Count == 0) FlushDiscordBatch();
        }

        private void Fail(Job job, string result)
        {
            PrintError("{0}: {1}", job.Target == null ? "Unknown plugin" : job.Target.Name, result);
            job.ErrorReported = true;
            Notify(job, result + ".", DiscordEvent.Failure);
            Complete(job, result, false);
        }

        private bool FindManifestResult(PluginTarget plugin, string json, out SearchResult found, out string error)
        {
            found = default(SearchResult); error = null;
            if (string.IsNullOrWhiteSpace(json) || IsHtml(json)) { error = "Manifest returned empty/HTML content"; return false; }
            try
            {
                var root = JObject.Parse(json);
                var name = (string)root["name"];
                if (!string.Equals(name, plugin.Name, StringComparison.OrdinalIgnoreCase))
                { error = "Manifest identity did not match " + plugin.Name; return false; }
                var releases = root["branches"]?["master"] as JObject;
                if (releases == null || !releases.Properties().Any()) { error = "Manifest contains no master releases"; return false; }
                JProperty latest = null;
                foreach (var release in releases.Properties())
                    if (latest == null || CompareVersions(release.Name, latest.Name) > 0) latest = release;
                if (latest == null) { error = "Manifest contains no usable release"; return false; }
                var downloadUrl = (string)latest.Value["download_url"];
                Uri downloadUri;
                if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out downloadUri) ||
                    !string.Equals(downloadUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(downloadUri.Host, "assets.umod.org", StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(Path.GetFileName(downloadUri.AbsolutePath), plugin.Name + "-" + latest.Name + ".cs", StringComparison.OrdinalIgnoreCase))
                { error = "Manifest download URL was invalid"; return false; }
                var maintainers = root["maintainers"] as JObject;
                var authors = maintainers == null ? new string[0] : maintainers.Properties()
                    .Select(x => (string)x.Value["name"]).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
                var author = string.Join(" / ", authors);
                if (!string.IsNullOrWhiteSpace(author) && !string.Equals(author, plugin.Author, StringComparison.OrdinalIgnoreCase))
                    PrintWarning("{0}: uMod maintainer differs from local author '{1}' to '{2}'; exact manifest identity retained.", plugin.Name, plugin.Author, author);
                var pageUrl = (string)root["url"];
                Uri pageUri;
                var slug = Uri.TryCreate(pageUrl, UriKind.Absolute, out pageUri) ? pageUri.AbsolutePath.Replace("/plugins/", "").Trim('/') : plugin.Name;
                found = new SearchResult { Version = latest.Name, Url = downloadUrl, Author = author, Slug = slug };
                return true;
            }
            catch (Exception ex) { error = "Invalid manifest response: " + ex.Message; return false; }
        }

        private bool FindResult(PluginTarget plugin, string json, out SearchResult found, out string error)
        {
            found = default(SearchResult); error = null;
            if (string.IsNullOrWhiteSpace(json) || IsHtml(json)) { error = "Search returned empty/HTML content"; return false; }
            try
            {
                var data = JObject.Parse(json)["data"] as JArray;
                if (data == null || data.Count == 0) { error = "No uMod search results"; return false; }
                var matches = new List<SearchResult>();
                foreach (var token in data)
                {
                    var item = token.ToObject<SearchResult>(); Uri uri;
                    if (string.IsNullOrEmpty(item.Url) || !Uri.TryCreate(item.Url, UriKind.Absolute, out uri)) continue;
                    if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                        !(string.Equals(uri.Host, "umod.org", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".umod.org", StringComparison.OrdinalIgnoreCase))) continue;
                    if (!string.Equals(Path.GetFileName(uri.AbsolutePath), plugin.Name + ".cs", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrEmpty(item.Version)) matches.Add(item);
                }
                if (matches.Count == 0) { error = "Search results did not contain the expected file " + plugin.Name + ".cs"; return false; }
                var authorMatch = matches.FirstOrDefault(x => string.Equals(x.Author, plugin.Author, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrEmpty(authorMatch.Url)) { found = authorMatch; return true; }
                if (_config.DisableAuthorCheck || matches.Count == 1)
                {
                    found = matches[0];
                    PrintWarning("{0}: uMod author changed from '{1}' to '{2}'; accepted the single exact filename match.", plugin.Name, plugin.Author, found.Author);
                    return true;
                }
                error = "Multiple exact filename matches were found and none matched author '" + plugin.Author + "'"; return false;
            }
            catch (Exception ex) { error = "Invalid search response: " + ex.Message; return false; }
        }

        private bool ValidSource(PluginTarget plugin, string source, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(source)) error = "empty response";
            else if (source.Length < _config.MinBytes) error = "suspiciously small response";
            else if (source.Length > _config.MaxBytes) error = "file exceeds size limit";
            else if (IsHtml(source)) error = "HTML response";
            else if (!source.Contains("[Info(") && !source.Contains("[Info (")) error = "missing Info attribute";
            else if (source.IndexOf("class " + plugin.Name, StringComparison.OrdinalIgnoreCase) < 0) error = "expected plugin class not found";
            return error == null;
        }

        private static bool IsHtml(string value)
        {
            var text = value.TrimStart();
            return text.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
                   text.IndexOf("<title>Too Many Requests</title>", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool Stage(PluginTarget plugin, string source, out string error)
        {
            error = null; var destination = StagedPath(plugin.Name); var temp = destination + ".download";
            try { Directory.CreateDirectory(StagingDirectory); File.WriteAllText(temp, source); if (File.Exists(destination)) File.Delete(destination); File.Move(temp, destination); return true; }
            catch (Exception ex) { TryDelete(temp); error = ex.Message; return false; }
        }

        private bool Apply(PluginTarget plugin, out string error, bool bulk = false)
        {
            error = null; var staged = StagedPath(plugin.Name);
            if (!File.Exists(staged)) { error = "No staged update for " + plugin.Name; return false; }
            try
            {
                var source = File.ReadAllText(staged); string validation;
                if (!ValidSource(plugin, source, out validation)) { error = "Staged file invalid: " + validation; return false; }
                PluginTarget stagedTarget;
                if (!TryParseTargetSource(source, staged, out stagedTarget)) { error = "Staged file metadata could not be read"; return false; }
                var updateType = UpdateKind(stagedTarget.Version, plugin.Version);
                if (updateType == UpdateType.Backward) { error = "Downgrade from v" + plugin.Version + " to v" + stagedTarget.Version + " is blocked"; return false; }
                if (bulk)
                {
                    if (PolicyFor(plugin) == UpdatePolicy.Ignore) { error = "plugin is ignored or external/private"; return false; }
                    bool allowed;
                    if (!_config.VersionSettings.TryGetValue(updateType, out allowed) || !allowed)
                    { error = updateType + " update requires `apu apply " + plugin.Name + "` after review"; return false; }
                }
                if (!ReplaceLiveFile(plugin, staged, "update", out error)) return false;
                File.Delete(staged); return true;
            }
            catch (Exception ex) { error = ex.Message; return false; }
        }

        private bool ReplaceLiveFile(PluginTarget plugin, string sourcePath, string reason, out string error)
        {
            var liveFile = plugin.FilePath;
            error = null; var temp = liveFile + ".apu-new";
            try
            {
                var source = File.ReadAllText(sourcePath); string validation;
                if (!ValidSource(plugin, source, out validation)) { error = "Source file invalid: " + validation; return false; }
                Directory.CreateDirectory(BackupDirectory);
                var backup = Path.Combine(BackupDirectory, plugin.Name + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + "-" + reason + ".cs");
                File.Copy(liveFile, backup, false);
                var configPath = PluginConfigPath(plugin.Name);
                if (File.Exists(configPath)) File.Copy(configPath, Path.ChangeExtension(backup, ".config.json"), false);
                File.Copy(sourcePath, temp, true);
                _selfReloads.Add(plugin.Name);
                timer.Once(30f, () => _selfReloads.Remove(plugin.Name));
                try { File.Replace(temp, liveFile, null); }
                catch (Exception ex) when (ex is IOException || ex is PlatformNotSupportedException) { File.Copy(temp, liveFile, true); File.Delete(temp); }
                PruneBackups(plugin.Name);
                return true;
            }
            catch (Exception ex) { _selfReloads.Remove(plugin.Name); TryDelete(temp); error = ex.Message; return false; }
        }

        private string[] BackupFiles(string pluginName)
        {
            if (!Directory.Exists(BackupDirectory)) return new string[0];
            return Directory.GetFiles(BackupDirectory, pluginName + "-*.cs").OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        }

        private void PruneBackups(string pluginName)
        {
            var files = BackupFiles(pluginName);
            for (var index = Math.Max(1, _config.BackupsToKeep); index < files.Length; index++)
            { TryDelete(Path.ChangeExtension(files[index], ".config.json")); TryDelete(files[index]); }
        }

        private UpdatePolicy PolicyFor(PluginTarget plugin)
        {
            string text; UpdatePolicy policy;
            if ((_config.PluginPolicies.TryGetValue(plugin.Name, out text) ||
                 !string.IsNullOrEmpty(plugin.Title) && _config.PluginPolicies.TryGetValue(plugin.Title, out text)) &&
                Enum.TryParse(text, true, out policy)) return policy;
            if (_config.BlacklistedPlugins.Any(x => string.Equals(x, plugin.Name, StringComparison.OrdinalIgnoreCase) || string.Equals(x, plugin.Title, StringComparison.OrdinalIgnoreCase)) ||
                _config.BlacklistedAuthors.Any(x => string.Equals(x, plugin.Author, StringComparison.OrdinalIgnoreCase)))
                return _config.NotifyBlacklisted ? UpdatePolicy.Notify : UpdatePolicy.Ignore;
            return Enum.TryParse(_config.DefaultPolicy, true, out policy) ? policy : UpdatePolicy.Stage;
        }

        private string StagingDirectory { get { return StoragePath(_config.StagingDirectory, "plugin_updates"); } }
        private string BackupDirectory { get { return StoragePath(_config.BackupDirectory, "plugin_backups"); } }
        private string LivePluginDirectory { get { return Path.GetFullPath(Path.GetDirectoryName(Filename) ?? Path.Combine(Interface.Oxide.RootDirectory, "oxide", "plugins")); } }
        private string PluginConfigDirectory { get { return Path.GetFullPath(Path.Combine(LivePluginDirectory, "..", "config")); } }
        private string StateFilePath { get { return Path.GetFullPath(Path.Combine(LivePluginDirectory, "..", "data", "AutomaticPluginUpdater.state.json")); } }
        private string PluginConfigPath(string pluginName) { return Path.Combine(PluginConfigDirectory, pluginName + ".json"); }
        private string PluginFilePath(Plugin plugin) { return Path.GetFullPath(Path.IsPathRooted(plugin.Filename) ? plugin.Filename : Path.Combine(LivePluginDirectory, Path.GetFileName(plugin.Filename))); }
        private bool IsPhysicalPlugin(Plugin plugin)
        {
            if (plugin == null || string.IsNullOrWhiteSpace(plugin.Filename) || !plugin.Filename.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) return false;
            try { var path = PluginFilePath(plugin); return File.Exists(path) && IsInsideDirectory(path, LivePluginDirectory); } catch { return false; }
        }
        private string StagedPath(string name) { return Path.Combine(StagingDirectory, name + ".cs"); }
        private string StoragePath(string configured, string fallback)
        { var path = string.IsNullOrWhiteSpace(configured) ? fallback : configured; return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Interface.Oxide.RootDirectory, path)); }
        private void EnsureDirectories()
        {
            try
            {
                var pluginDirectory = Path.GetFullPath(Path.GetDirectoryName(Filename) ?? "");
                if (IsInsideDirectory(StagingDirectory, pluginDirectory) || IsInsideDirectory(BackupDirectory, pluginDirectory))
                    throw new InvalidOperationException("Staging and backup directories must be outside the live plugin directory.");
                Directory.CreateDirectory(StagingDirectory); Directory.CreateDirectory(BackupDirectory);
            }
            catch (Exception ex) { PrintError("Storage setup failed: " + ex.Message); }
        }
        private static bool IsInsideDirectory(string child, string parent)
        { return Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar).StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase); }
        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        private static bool Retryable(int code) { return code == 0 || code == 408 || code == 429 || code >= 500; }

        private void LoadState()
        {
            try
            {
                if (File.Exists(StateFilePath)) _state = JsonConvert.DeserializeObject<PersistentState>(File.ReadAllText(StateFilePath)) ?? new PersistentState();
                if (_state.Plugins == null) _state.Plugins = new Dictionary<string, CheckState>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) { PrintWarning("Could not load check history; starting fresh: " + ex.Message); _state = new PersistentState(); }
        }

        private void SaveState()
        {
            try { Directory.CreateDirectory(Path.GetDirectoryName(StateFilePath)); File.WriteAllText(StateFilePath, JsonConvert.SerializeObject(_state, Formatting.Indented)); }
            catch (Exception ex) { PrintWarning("Could not save check history: " + ex.Message); }
        }

        private bool ResumeAudit()
        {
            var saved = _state.Audit;
            if (saved == null || saved.PendingPlugins == null || saved.PendingPlugins.Count == 0) return false;
            var audit = new AuditSession
            {
                Id = ++_nextAuditId,
                StartedUtc = saved.StartedUtc == default(DateTime) ? DateTime.UtcNow : saved.StartedUtc,
                Results = saved.Results ?? new List<AuditResult>()
            };
            _audits[audit.Id] = audit;
            var targets = InstalledTargets().ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
            foreach (var name in saved.PendingPlugins.ToArray())
            {
                PluginTarget target;
                if (targets.TryGetValue(name, out target) && EnqueueTarget(target, null, true, audit.Id)) continue;
                audit.Results.Add(new AuditResult { PluginName = name, LocalVersion = "Unknown", Result = "Plugin file unavailable after updater reload", PageUrl = "https://umod.org/plugins?query=" + Uri.EscapeDataString(name) });
                saved.PendingPlugins.RemoveAll(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
            }
            audit.Expected = audit.Results.Count + saved.PendingPlugins.Count;
            saved.Results = audit.Results;
            SaveState();
            Puts("Resuming interrupted full audit: {0} complete, {1} remaining.", audit.Results.Count, saved.PendingPlugins.Count);
            if (saved.PendingPlugins.Count == 0) FinishAudit(audit);
            return true;
        }

        private static UpdateType UpdateKind(string value, string oldValue)
        {
            int a, b, c, oldA, oldB, oldC;
            if (!ParseVersion(value, out a, out b, out c) || !ParseVersion(oldValue, out oldA, out oldB, out oldC)) return UpdateType.Unknown;
            if (a < oldA || a == oldA && b < oldB || a == oldA && b == oldB && c <= oldC) return UpdateType.Backward;
            return a > oldA ? UpdateType.Major : b > oldB ? UpdateType.Minor : UpdateType.Patch;
        }
        private static bool SameVersion(string x, string y) { int a,b,c,d,e,f; return ParseVersion(x,out a,out b,out c) && ParseVersion(y,out d,out e,out f) && a==d && b==e && c==f; }
        private static int CompareVersions(string x, string y)
        {
            int a,b,c,d,e,f;
            if (ParseVersion(x,out a,out b,out c) && ParseVersion(y,out d,out e,out f))
            { var major=a.CompareTo(d); if(major!=0)return major; var minor=b.CompareTo(e); return minor!=0?minor:c.CompareTo(f); }
            return string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
        }
        private static bool ParseVersion(string value, out int a, out int b, out int c)
        { a=b=c=0; if (string.IsNullOrWhiteSpace(value)) return false; var p=value.Split(new[]{'-','+'},2)[0].Split('.'); return p.Length<=3 && int.TryParse(p[0],out a) && (p.Length<2 || int.TryParse(p[1],out b)) && (p.Length<3 || int.TryParse(p[2],out c)); }

        private static string VersionAdvisory(string localVersion, string remoteVersion, UpdateType type)
        {
            if (type == UpdateType.Unknown) return "non-standard version change; verify plugin identity and release notes";
            if (type != UpdateType.Major) return null;
            int localMajor, localMinor, localPatch, remoteMajor, remoteMinor, remotePatch;
            if (ParseVersion(localVersion, out localMajor, out localMinor, out localPatch) && ParseVersion(remoteVersion, out remoteMajor, out remoteMinor, out remotePatch) && remoteMajor - localMajor > 1)
                return "large major-version jump from v" + localVersion + " to v" + remoteVersion + "; verify plugin identity and provider";
            return "major-version change; review release notes before applying";
        }

        private void Notify(Job job, string message, DiscordEvent eventType)
        {
            // Full audits send one report after every check has finished.
            if (job.AuditId != 0) return;
            var pluginName = job.Target == null ? "Unknown plugin" : job.Target.Name;
            var settings = _config.DiscordSettings;
            if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.WebhookUrl)) return;
            if ((job.Caller == null && !settings.NotifyAutomaticChecks) ||
                (job.Caller != null && !settings.NotifyManualSingleChecks)) return;
            var safetyNotice = eventType == DiscordEvent.Blocked || !string.IsNullOrWhiteSpace(job.Advisory);
            if (safetyNotice)
            {
                if (!settings.NotifySafetyBlocks) return;
            }
            else if (eventType == DiscordEvent.Available && !settings.NotifyAvailable || eventType == DiscordEvent.Staged && !settings.NotifyStaged ||
                eventType == DiscordEvent.Installed && !settings.NotifyInstalled || eventType == DiscordEvent.Failure && !settings.NotifyFailures) return;
            _discordBatch.Add(new DiscordNotice
            {
                PluginName = pluginName,
                LocalAuthor = job.Target == null ? "Unknown" : job.Target.Author,
                RemoteAuthor = job.Result.Author,
                LocalVersion = job.Target == null ? "Unknown" : job.Target.Version,
                RemoteVersion = job.Result.Version,
                Message = message,
                Advisory = job.Advisory,
                Event = eventType,
                PageUrl = PluginPageUrl(job)
            });
        }

        private void RecordAuditResult(Job job, string result)
        {
            if (job.AuditId == 0) return;
            AuditSession audit;
            if (!_audits.TryGetValue(job.AuditId, out audit)) return;
            var auditResult = new AuditResult
            {
                PluginName = job.Target == null ? "Unknown plugin" : job.Target.Name,
                LocalVersion = job.Target == null ? "Unknown" : job.Target.Version,
                RemoteVersion = job.Result.Version,
                IsLoaded = job.Target != null && job.Target.IsLoaded,
                Result = result,
                PageUrl = PluginPageUrl(job)
            };
            audit.Results.Add(auditResult);
            if (_state.Audit != null)
            {
                if (_state.Audit.Results == null) _state.Audit.Results = new List<AuditResult>();
                if (!ReferenceEquals(_state.Audit.Results, audit.Results)) _state.Audit.Results.Add(auditResult);
                if (_state.Audit.PendingPlugins != null)
                    _state.Audit.PendingPlugins.RemoveAll(x => string.Equals(x, auditResult.PluginName, StringComparison.OrdinalIgnoreCase));
                SaveState();
            }
            if (audit.Results.Count < audit.Expected) return;
            FinishAudit(audit);
        }

        private void FinishAudit(AuditSession audit)
        {
            _audits.Remove(audit.Id);
            _state.Audit = null;
            SaveState();
            var discordEnabled = _config.DiscordSettings.Enabled && _config.DiscordSettings.NotifyManualAudits && !string.IsNullOrWhiteSpace(_config.DiscordSettings.WebhookUrl);
            var completedKey = discordEnabled ? "AuditCompleteDiscord" : "AuditCompleteNoDiscord";
            var completed = string.Format(lang.GetMessage(completedKey, this), audit.Results.Count);
            Reply(audit.Caller, completedKey, audit.Results.Count);
            if (audit.Caller == null) Puts(completed);
            SendAuditSummary(audit);
            if (_queue.Count == 0 && !_busy && _queueTimer == null) FlushDiscordBatch();
        }

        private void SendAuditSummary(AuditSession audit)
        {
            if (!_config.DiscordSettings.Enabled || !_config.DiscordSettings.NotifyManualAudits || string.IsNullOrWhiteSpace(_config.DiscordSettings.WebhookUrl)) return;
            const int perEmbed = 20;
            var totalParts = (int)Math.Ceiling(audit.Results.Count / (double)perEmbed);
            for (var offset = 0; offset < audit.Results.Count; offset += perEmbed)
            {
                var chunk = audit.Results.Skip(offset).Take(perEmbed).ToArray();
                var part = offset / perEmbed + 1;
                var fields = chunk.Select(x => new DiscordField
                {
                    name = AuditIcon(x.Result) + " " + x.PluginName,
                    value = "Local: `" + x.LocalVersion + "`" + (string.IsNullOrWhiteSpace(x.RemoteVersion) ? "" : " • Remote: `" + x.RemoteVersion + "`") +
                        "\nState: " + (x.IsLoaded ? "Loaded" : "Unloaded/compile-failed") + "\nResult: " + x.Result +
                        "\n[Plugin page](" + x.PageUrl + ")" + ReleaseNotesLink(x.PageUrl),
                    inline = false
                }).ToArray();
                var embed = new DiscordEmbed
                {
                    title = "Full Plugin Audit" + (totalParts > 1 ? " (part " + part + "/" + totalParts + ")" : ""),
                    description = audit.Results.Count + " installed plugins checked • completed in " + Math.Max(1, (int)(DateTime.UtcNow - audit.StartedUtc).TotalMinutes) + " minute(s)",
                    color = audit.Results.Any(x => IsSafetyResult(x.Result)) ? 16753920 : 5238078,
                    fields = fields,
                    footer = new DiscordFooter { text = "Automatic Plugin Updater v2 • complete manual check" },
                    timestamp = DateTime.UtcNow.ToString("o")
                };
                var payload = JsonConvert.SerializeObject(new { embeds = new[] { embed } });
                var label = "full audit part " + part + "/" + totalParts;
                timer.Once(Math.Max(0.1f, (part - 1) * 2f), () => SendDiscordPayload(payload, label));
            }
        }

        private static string AuditIcon(string result)
        {
            if (result.StartsWith("Up to date", StringComparison.OrdinalIgnoreCase)) return "✅";
            if (result.StartsWith("Staged", StringComparison.OrdinalIgnoreCase)) return "📦";
            if (result.StartsWith("Installed", StringComparison.OrdinalIgnoreCase)) return "⬆️";
            if (result.IndexOf("available", StringComparison.OrdinalIgnoreCase) >= 0) return "🔔";
            return "⚠️";
        }

        private static bool IsSafetyResult(string result)
        {
            return !string.IsNullOrWhiteSpace(result) && (result.StartsWith("[Safety]", StringComparison.OrdinalIgnoreCase) ||
                result.IndexOf("review required", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private string PluginPageUrl(Job job)
        {
            if (!string.IsNullOrWhiteSpace(job.Result.Slug)) return "https://umod.org/plugins/" + job.Result.Slug.Trim('/');
            var title = job.Target == null ? "" : (string.IsNullOrWhiteSpace(job.Target.Title) ? job.Target.Name : job.Target.Title);
            return "https://umod.org/plugins?query=" + Uri.EscapeDataString(title);
        }

        private void FlushDiscordBatch()
        {
            if (_discordBatch.Count == 0 || !_config.DiscordSettings.Enabled || string.IsNullOrWhiteSpace(_config.DiscordSettings.WebhookUrl)) return;
            var notices = _discordBatch.ToArray(); _discordBatch.Clear();
            const int perEmbed = 20;
            var totalParts = (int)Math.Ceiling(notices.Length / (double)perEmbed);
            for (var offset = 0; offset < notices.Length; offset += perEmbed)
            {
                var chunk = notices.Skip(offset).Take(perEmbed).ToArray();
                var part = offset / perEmbed + 1;
                var counts = chunk.GroupBy(x => x.Event).Select(x => x.Key + ": " + x.Count()).ToArray();
                var fields = chunk.Select(x => new DiscordField
                {
                    name = EventIcon(x.Event) + " " + x.PluginName,
                    value = DiscordNoticeText(x),
                    inline = false
                }).ToArray();
                var embed = new DiscordEmbed
                {
                    title = "Plugin Update Summary" + (totalParts > 1 ? " (part " + part + "/" + totalParts + ")" : ""),
                    description = string.Join(" • ", counts) + (totalParts > 1 ? " • " + chunk.Length + " shown in this part" : ""),
                    color = chunk.Any(x => x.Event == DiscordEvent.Blocked || x.Event == DiscordEvent.Failure) ? 15158332 :
                        chunk.Any(x => !string.IsNullOrWhiteSpace(x.Advisory)) ? 16753920 : 5238078,
                    fields = fields,
                    footer = new DiscordFooter { text = "Automatic Plugin Updater v2 • " + notices.Length + " total update event(s)" + (totalParts > 1 ? " • part " + part + "/" + totalParts : "") },
                    timestamp = DateTime.UtcNow.ToString("o")
                };
                var payload = JsonConvert.SerializeObject(new { embeds = new[] { embed } });
                var delay = (offset / perEmbed) * 2f;
                var label = "update summary part " + part + "/" + totalParts;
                timer.Once(Math.Max(0.1f, delay), () => SendDiscordPayload(payload, label));
            }
        }

        private void SendDiscordPayload(string payload, string label = "notification")
        {
            webrequest.Enqueue(_config.DiscordSettings.WebhookUrl, payload, (code, body) =>
            {
                if (code == 200 || code == 201 || code == 204)
                {
                    if (_config.Verbose) Puts("Discord {0} delivered (HTTP {1}).", label, code);
                }
                else PrintError("Discord {0} failed: HTTP {1}: {2}", label, code, string.IsNullOrEmpty(body) ? "no response" : body);
            },
                this, RequestMethod.POST, new Dictionary<string,string>{{"Content-Type","application/json"}});
        }

        private static string ReleaseNotesLink(string pageUrl)
        {
            if (string.IsNullOrWhiteSpace(pageUrl) || pageUrl.IndexOf("/plugins?", StringComparison.OrdinalIgnoreCase) >= 0) return "";
            return " • [Review release notes](" + pageUrl.TrimEnd('/') + "/updates)";
        }

        private static string EventIcon(DiscordEvent value)
        {
            switch (value) { case DiscordEvent.Staged: return "📦"; case DiscordEvent.Installed: return "✅"; case DiscordEvent.Failure: return "❌"; case DiscordEvent.Blocked: return "🛑"; default: return "🔔"; }
        }

        private static string DiscordNoticeText(DiscordNotice notice)
        {
            var lines = new List<string>();
            if (!string.IsNullOrWhiteSpace(notice.RemoteVersion))
                lines.Add("Version: `" + notice.LocalVersion + "` → `" + notice.RemoteVersion + "`");
            else lines.Add("Current version: `" + notice.LocalVersion + "`");
            lines.Add("Status: **" + notice.Event + "**");
            if (!string.IsNullOrWhiteSpace(notice.Message) && (notice.Event == DiscordEvent.Failure || notice.Event == DiscordEvent.Blocked)) lines.Add("Reason: " + notice.Message);
            if (!string.IsNullOrWhiteSpace(notice.Advisory)) lines.Add("Warning: **" + notice.Advisory + "**");
            var localAuthor = string.IsNullOrWhiteSpace(notice.LocalAuthor) ? "Unknown" : notice.LocalAuthor;
            var remoteAuthor = string.IsNullOrWhiteSpace(notice.RemoteAuthor) ? localAuthor : notice.RemoteAuthor;
            lines.Add(string.Equals(localAuthor, remoteAuthor, StringComparison.OrdinalIgnoreCase)
                ? "Author: " + remoteAuthor
                : "Maintainer: " + remoteAuthor + " *(local file: " + localAuthor + ")*");
            lines.Add("[Plugin page](" + notice.PageUrl + ")" + ReleaseNotesLink(notice.PageUrl));
            return string.Join("\n", lines.ToArray());
        }

        private void SendDiscordTest(IPlayer caller)
        {
            if (!_config.DiscordSettings.Enabled) { Reply(caller, "DiscordDisabled"); return; }
            if (string.IsNullOrWhiteSpace(_config.DiscordSettings.WebhookUrl)) { Reply(caller, "DiscordUrlMissing"); return; }
            Reply(caller, "DiscordTesting");
            var testEmbed = new DiscordEmbed
            {
                title = "✅ Webhook connection test",
                description = "Automatic Plugin Updater can send grouped update summaries to this channel.",
                color = 5238078,
                fields = new[] { new DiscordField { name = "Status", value = "Webhook delivery successful", inline = false } },
                footer = new DiscordFooter { text = "Automatic Plugin Updater v2" },
                timestamp = DateTime.UtcNow.ToString("o")
            };
            var payload = JsonConvert.SerializeObject(new { embeds = new[] { testEmbed } });
            webrequest.Enqueue(_config.DiscordSettings.WebhookUrl, payload, (code, body) =>
            {
                if (code == 200 || code == 201 || code == 204) Reply(caller, "DiscordTestSucceeded", code);
                else { Reply(caller, "DiscordTestFailed", code); PrintError("Discord webhook test failed: HTTP {0}: {1}", code, string.IsNullOrEmpty(body) ? "no response" : body); }
            }, this, RequestMethod.POST, new Dictionary<string,string>{{"Content-Type","application/json"}});
        }

        private enum UpdatePolicy { Ignore, Notify, Stage, Install }
        private enum UpdateType { Backward, Minor, Patch, Major, Unknown }
        private enum DiscordEvent { Available, Staged, Installed, Failure, Blocked }
        private class Job { public PluginTarget Target; public IPlayer Caller; public UpdatePolicy Policy; public bool InstallBlockedByVersionPolicy; public bool ErrorReported; public bool VersionMismatchReported; public bool UseLegacySearch; public string Advisory; public UpdateType BlockedUpdateType; public int Attempts; public int SearchAttempt; public int AuditId; public SearchResult Result; }
        private class PluginTarget
        {
            public Plugin Plugin; public string Name; public string Title; public string Author; public string Version; public string LoadedVersion; public string FilePath;
            [JsonIgnore] public bool IsLoaded { get { return Plugin != null && Plugin.IsLoaded; } }
            [JsonIgnore] public bool HasVersionMismatch { get { return IsLoaded && !string.IsNullOrWhiteSpace(LoadedVersion) && !string.Equals(Version, LoadedVersion, StringComparison.OrdinalIgnoreCase); } }
        }
        private class PersistentState { public Dictionary<string, CheckState> Plugins = new Dictionary<string, CheckState>(StringComparer.OrdinalIgnoreCase); public PersistentAudit Audit; }
        private class PersistentAudit { public DateTime StartedUtc; public List<string> PendingPlugins = new List<string>(); public List<AuditResult> Results = new List<AuditResult>(); }
        private class CheckState { public DateTime CheckedUtc; public string LocalVersion; public string Result; }
        private struct SearchResult
        {
            [JsonProperty("latest_release_version")] public string Version;
            [JsonProperty("download_url")] public string Url;
            [JsonProperty("author")] public string Author;
            [JsonProperty("slug")] public string Slug;
        }
        private class DiscordNotice { public string PluginName; public string LocalAuthor; public string RemoteAuthor; public string LocalVersion; public string RemoteVersion; public string Message; public string Advisory; public string PageUrl; public DiscordEvent Event; }
        private class AuditSession { public int Id; public int Expected; public IPlayer Caller; public DateTime StartedUtc; public List<AuditResult> Results = new List<AuditResult>(); }
        private class AuditResult { public string PluginName; public string LocalVersion; public string RemoteVersion; public bool IsLoaded; public string Result; public string PageUrl; }
        private class DiscordField { public string name; public string value; public bool inline; }
        private class DiscordFooter { public string text; }
        private class DiscordEmbed { public string title; public string description; public int color; public DiscordField[] fields; public DiscordFooter footer; public string timestamp; }

        private class ConfigData
        {
            [JsonProperty("Configuration Version")] public int Version = 2;
            [JsonProperty("Checks and Scheduling")] public ScheduleConfig Schedule = new ScheduleConfig();
            [JsonProperty("Update Policies")] public PolicyConfig Policies = new PolicyConfig();
            [JsonProperty("Storage and Backups")] public StorageConfig Storage = new StorageConfig();
            [JsonProperty("Network and Retry Limits")] public NetworkConfig Network = new NetworkConfig();
            [JsonProperty("Discord Notifications")] public DiscordConfig DiscordSettings = new DiscordConfig();
            [JsonProperty("Logging")] public LoggingConfig Logging = new LoggingConfig();

            [JsonIgnore] public bool CheckOnStart { get { return Schedule.CheckOnStart; } }
            [JsonIgnore] public bool DisableStartupCheck { get { return false; } }
            [JsonIgnore] public bool CheckNewPlugins { get { return Schedule.CheckNewPlugins; } }
            [JsonIgnore] public bool RoutineChecks { get { return Schedule.RoutineChecks; } }
            [JsonIgnore] public float RoutineMinutes { get { return Schedule.RoutineMinutes; } }
            [JsonIgnore] public float MinimumRecheckMinutes { get { return Schedule.MinimumRecheckMinutes; } }
            [JsonIgnore] public List<string> BlacklistedPlugins { get { return Policies.IgnoredPlugins; } }
            [JsonIgnore] public List<string> BlacklistedAuthors { get { return Policies.IgnoredAuthors; } }
            [JsonIgnore] public bool NotifyBlacklisted { get { return Policies.NotifyIgnored; } }
            [JsonIgnore] public bool DisableAuthorCheck { get { return Policies.DisableAuthorCheck; } }
            [JsonIgnore] public Dictionary<UpdateType,bool> VersionSettings { get { return Policies.VersionSettings; } }
            [JsonIgnore] public string DefaultPolicy { get { return Policies.DefaultPolicy; } }
            [JsonIgnore] public Dictionary<string,string> PluginPolicies { get { return Policies.PluginPolicies; } }
            [JsonIgnore] public string StagingDirectory { get { return Storage.StagingDirectory; } }
            [JsonIgnore] public string BackupDirectory { get { return Storage.BackupDirectory; } }
            [JsonIgnore] public int BackupsToKeep { get { return Storage.BackupsToKeep; } }
            [JsonIgnore] public float RequestSeconds { get { return Network.RequestSeconds; } }
            [JsonIgnore] public int MaxRetries { get { return Network.MaxRetries; } }
            [JsonIgnore] public float InitialRetrySeconds { get { return Network.InitialRetrySeconds; } }
            [JsonIgnore] public float MaxRetrySeconds { get { return Network.MaxRetrySeconds; } }
            [JsonIgnore] public int MinBytes { get { return Network.MinBytes; } }
            [JsonIgnore] public int MaxBytes { get { return Network.MaxBytes; } }
            [JsonIgnore] public bool AllowLegacySearchFallback { get { return Network.AllowLegacySearchFallback; } }
            [JsonIgnore] public bool Verbose { get { return Logging.Verbose; } }
            public static ConfigData Defaults() { return new ConfigData(); }
        }

        private class ScheduleConfig
        {
            [JsonProperty("Check all installed plugins when updater loads")] public bool CheckOnStart = false;
            [JsonProperty("Check all loaded plugins when updater loads")] private bool LegacyCheckOnStart { set { CheckOnStart = value; } }
            [JsonProperty("Check plugins when they are loaded")] public bool CheckNewPlugins = false;
            [JsonProperty("Enable routine checks")] public bool RoutineChecks = false;
            [JsonProperty("Routine check interval (minutes)")] public float RoutineMinutes = 1440f;
            [JsonProperty("Minimum minutes before rechecking the same local version")] public float MinimumRecheckMinutes = 720f;
        }
        private class PolicyConfig
        {
            [JsonProperty("Default policy (Ignore, Notify, Stage, Install)")] public string DefaultPolicy = "Stage";
            [JsonProperty("Per-plugin policies")] public Dictionary<string,string> PluginPolicies = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            [JsonProperty("Ignored plugins")] public List<string> IgnoredPlugins = new List<string>();
            [JsonProperty("Ignored authors")] public List<string> IgnoredAuthors = new List<string>();
            [JsonProperty("Notify when ignored plugins have updates")] public bool NotifyIgnored = false;
            [JsonProperty("Disable author matching (less safe)")] public bool DisableAuthorCheck = false;
            [JsonProperty("Allowed automatic installation version changes")] public Dictionary<UpdateType,bool> VersionSettings = new Dictionary<UpdateType,bool>
                {{UpdateType.Backward,false},{UpdateType.Major,false},{UpdateType.Minor,true},{UpdateType.Patch,true},{UpdateType.Unknown,false}};
            [JsonProperty("Allowed version changes")] private Dictionary<UpdateType,bool> LegacyVersionSettings { set { if (value != null) VersionSettings = value; } }
        }
        private class StorageConfig
        {
            [JsonProperty("Staging directory (relative to server root)")] public string StagingDirectory = "plugin_updates";
            [JsonProperty("Backup directory (relative to server root)")] public string BackupDirectory = "plugin_backups";
            [JsonProperty("Backups to keep per plugin")] public int BackupsToKeep = 5;
        }
        private class NetworkConfig
        {
            [JsonProperty("Allow legacy uMod website search fallback")] public bool AllowLegacySearchFallback = false;
            [JsonProperty("Seconds between web requests")] public float RequestSeconds = 8f;
            [JsonProperty("Maximum retries")] public int MaxRetries = 4;
            [JsonProperty("Initial retry delay (seconds)")] public float InitialRetrySeconds = 30f;
            [JsonProperty("Maximum retry delay (seconds)")] public float MaxRetrySeconds = 900f;
            [JsonProperty("Minimum accepted plugin size (bytes)")] public int MinBytes = 100;
            [JsonProperty("Maximum accepted plugin size (bytes)")] public int MaxBytes = 5242880;
        }
        private class DiscordConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = false;
            [JsonProperty("Webhook URL")] public string WebhookUrl = "";
            [JsonProperty("Send notifications for automatic checks")] public bool NotifyAutomaticChecks = true;
            [JsonProperty("Send notifications for manual single-plugin checks")] public bool NotifyManualSingleChecks = false;
            [JsonProperty("Send complete reports for manual full audits")] public bool NotifyManualAudits = true;
            [JsonProperty("Notify when an update is available but not installed")] public bool NotifyAvailable = true;
            [JsonProperty("Notify when an update is staged")] public bool NotifyStaged = true;
            [JsonProperty("Notify when an update is installed")] public bool NotifyInstalled = true;
            [JsonProperty("Notify when an update fails")] public bool NotifyFailures = true;
            [JsonProperty("Notify when an update is blocked or requires safety review")] public bool NotifySafetyBlocks = true;
        }
        private class LoggingConfig { [JsonProperty("Log detailed queue activity")] public bool Verbose = false; }

        private class LegacyConfig
        {
            [JsonProperty("Check Currently Loaded Plugins When This Plugin Enables?")] public bool CheckOnStart;
            [JsonProperty("Check Newly Loaded Plugins?")] public bool CheckNewPlugins;
            [JsonProperty("Blacklisted Plugins (won't check for update)")] public List<string> BlacklistedPlugins;
            [JsonProperty("Blacklisted Authors (won't check for update)")] public List<string> BlacklistedAuthors;
            [JsonProperty("Update Version Settings (which updates to not go through with)")] public Dictionary<UpdateType,bool> VersionSettings;
            [JsonProperty("Do Routine Checks?")] public bool RoutineChecks;
            [JsonProperty("Routine Interval Time (Minutes)")] public float RoutineMinutes;
            [JsonProperty("Notify on Blacklisted Update?")] public bool NotifyBlacklisted;
            [JsonProperty("Use Discord Webhooks?")] public bool Discord;
            [JsonProperty("Disable Author Checking?")] public bool DisableAuthorCheck;
            [JsonProperty("Discord Webhook")] public string Webhook;
            [JsonProperty("Default Update Policy (Ignore, Notify, Stage, Install)")] public string DefaultPolicy;
            [JsonProperty("Per-Plugin Update Policies")] public Dictionary<string,string> PluginPolicies;
            [JsonProperty("Staging Directory (relative to server root)")] public string StagingDirectory;
            [JsonProperty("Backup Directory (relative to server root)")] public string BackupDirectory;
            [JsonProperty("Seconds Between Web Requests")] public float RequestSeconds;
            [JsonProperty("Maximum Request Retries")] public int MaxRetries;
            [JsonProperty("Initial Retry Delay (Seconds)")] public float InitialRetrySeconds;
            [JsonProperty("Maximum Retry Delay (Seconds)")] public float MaxRetrySeconds;
            [JsonProperty("Minimum Plugin Size (Bytes)")] public int MinBytes;
            [JsonProperty("Maximum Plugin Size (Bytes)")] public int MaxBytes;
            [JsonProperty("Log Detailed Activity?")] public bool Verbose;
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                var raw = Config.ReadObject<JObject>();
                if (raw == null) throw new Exception("empty config");
                if (raw["Configuration Version"] != null || raw["Checks and Scheduling"] != null) _config = raw.ToObject<ConfigData>();
                else { _config = MigrateLegacy(raw.ToObject<LegacyConfig>()); PrintWarning("Migrated the legacy configuration to the grouped v2 format."); }
            }
            catch (Exception ex) { PrintError("Config error ({0}); using safe defaults.", ex.Message); _config = ConfigData.Defaults(); }
            MergeDefaults(); SaveConfig();
        }
        private ConfigData MigrateLegacy(LegacyConfig old)
        {
            var c = ConfigData.Defaults(); if (old == null) return c;
            c.Schedule.CheckOnStart=old.CheckOnStart; c.Schedule.CheckNewPlugins=old.CheckNewPlugins; c.Schedule.RoutineChecks=old.RoutineChecks; if(old.RoutineMinutes>0)c.Schedule.RoutineMinutes=old.RoutineMinutes;
            if(old.BlacklistedPlugins!=null)c.Policies.IgnoredPlugins=old.BlacklistedPlugins; if(old.BlacklistedAuthors!=null)c.Policies.IgnoredAuthors=old.BlacklistedAuthors;
            if(old.VersionSettings!=null)c.Policies.VersionSettings=old.VersionSettings; c.Policies.NotifyIgnored=old.NotifyBlacklisted; c.Policies.DisableAuthorCheck=old.DisableAuthorCheck;
            if(!string.IsNullOrWhiteSpace(old.DefaultPolicy))c.Policies.DefaultPolicy=old.DefaultPolicy; if(old.PluginPolicies!=null)c.Policies.PluginPolicies=old.PluginPolicies;
            if(!string.IsNullOrWhiteSpace(old.StagingDirectory))c.Storage.StagingDirectory=old.StagingDirectory; if(!string.IsNullOrWhiteSpace(old.BackupDirectory))c.Storage.BackupDirectory=old.BackupDirectory;
            if(old.RequestSeconds>0)c.Network.RequestSeconds=old.RequestSeconds; if(old.MaxRetries>0)c.Network.MaxRetries=old.MaxRetries; if(old.InitialRetrySeconds>0)c.Network.InitialRetrySeconds=old.InitialRetrySeconds;
            if(old.MaxRetrySeconds>0)c.Network.MaxRetrySeconds=old.MaxRetrySeconds; if(old.MinBytes>0)c.Network.MinBytes=old.MinBytes; if(old.MaxBytes>0)c.Network.MaxBytes=old.MaxBytes;
            c.DiscordSettings.Enabled=old.Discord; c.DiscordSettings.WebhookUrl=old.Webhook ?? ""; c.Logging.Verbose=old.Verbose; return c;
        }
        private void MergeDefaults()
        {
            var d=ConfigData.Defaults(); if(_config==null)_config=d;
            if(_config.Schedule==null)_config.Schedule=d.Schedule; if(_config.Policies==null)_config.Policies=d.Policies; if(_config.Storage==null)_config.Storage=d.Storage;
            if(_config.Network==null)_config.Network=d.Network; if(_config.DiscordSettings==null)_config.DiscordSettings=d.DiscordSettings; if(_config.Logging==null)_config.Logging=d.Logging;
            if(_config.Policies.IgnoredPlugins==null)_config.Policies.IgnoredPlugins=d.Policies.IgnoredPlugins; if(_config.Policies.IgnoredAuthors==null)_config.Policies.IgnoredAuthors=d.Policies.IgnoredAuthors;
            if(_config.Policies.PluginPolicies==null)_config.Policies.PluginPolicies=d.Policies.PluginPolicies; if(_config.Policies.VersionSettings==null)_config.Policies.VersionSettings=d.Policies.VersionSettings;
            foreach(var x in d.Policies.VersionSettings)if(!_config.Policies.VersionSettings.ContainsKey(x.Key))_config.Policies.VersionSettings[x.Key]=x.Value;
            if(string.IsNullOrWhiteSpace(_config.Policies.DefaultPolicy))_config.Policies.DefaultPolicy=d.Policies.DefaultPolicy;
            if(string.IsNullOrWhiteSpace(_config.Storage.StagingDirectory))_config.Storage.StagingDirectory=d.Storage.StagingDirectory; if(string.IsNullOrWhiteSpace(_config.Storage.BackupDirectory))_config.Storage.BackupDirectory=d.Storage.BackupDirectory;
            if(_config.Storage.BackupsToKeep<1)_config.Storage.BackupsToKeep=d.Storage.BackupsToKeep; if(_config.Schedule.RoutineMinutes<5)_config.Schedule.RoutineMinutes=d.Schedule.RoutineMinutes;
            if(_config.Schedule.MinimumRecheckMinutes<0)_config.Schedule.MinimumRecheckMinutes=d.Schedule.MinimumRecheckMinutes;
            if(_config.Network.RequestSeconds<1)_config.Network.RequestSeconds=d.Network.RequestSeconds; if(_config.Network.MaxRetries<1)_config.Network.MaxRetries=d.Network.MaxRetries;
            if(_config.Network.InitialRetrySeconds<1)_config.Network.InitialRetrySeconds=d.Network.InitialRetrySeconds; if(_config.Network.MaxRetrySeconds<_config.Network.InitialRetrySeconds)_config.Network.MaxRetrySeconds=d.Network.MaxRetrySeconds;
            if(_config.Network.MinBytes<1)_config.Network.MinBytes=d.Network.MinBytes; if(_config.Network.MaxBytes<_config.Network.MinBytes)_config.Network.MaxBytes=d.Network.MaxBytes;
            _config.Version=2;
        }
        protected override void LoadDefaultConfig() { _config=ConfigData.Defaults(); PrintWarning("Created safe updater configuration."); }
        protected override void SaveConfig() { Config.WriteObject(_config,true); }
    }
}
