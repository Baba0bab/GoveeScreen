// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2025-2026 Baba0bab — GoveeScreen

using System.IO;
using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace GoveeScreenV2.Core;

public enum AppUpdateState
{
    Unavailable,   // copie non installée (version de développement) : pas de mises à jour
    Idle,
    Checking,
    UpToDate,
    Downloading,
    Ready,         // téléchargée, en attente d'installation
    Error
}

/// <summary>
/// Mises à jour de l'application depuis les Releases GitHub (Velopack) : recherche, téléchargement
/// en arrière-plan (delta si possible), puis installation à la fermeture ou au redémarrage.
/// Les événements sont levés sur n'importe quel thread.
/// </summary>
public sealed class AppUpdater
{
    public const string RepositoryUrl = "https://github.com/Baba0bab/GoveeScreen";

    /// <summary>Source de test (dossier local ou URL) utilisée à la place de GitHub.</summary>
    private const string SourceOverrideVariable = "GOVEESCREEN_UPDATE_SOURCE";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateManager _manager;
    private UpdateInfo? _pending;
    private bool _includePrerelease;

    public AppUpdateState State { get; private set; }
    public string? AvailableVersion { get; private set; }
    public string? ReleaseNotes { get; private set; }
    public int Progress { get; private set; }
    public string? Error { get; private set; }
    public DateTime? LastCheckUtc { get; private set; }

    public event Action? StateChanged;

    public AppUpdater(bool includePrerelease)
    {
        _includePrerelease = includePrerelease;
        _manager = CreateManager(includePrerelease);
        State = _manager.IsInstalled ? AppUpdateState.Idle : AppUpdateState.Unavailable;

        // Mise à jour déjà téléchargée lors d'une session précédente
        if (_manager.IsInstalled && _manager.UpdatePendingRestart is { } pending)
        {
            AvailableVersion = pending.Version.ToString();
            ReleaseNotes = pending.NotesMarkdown;
            State = AppUpdateState.Ready;
        }
    }

    public bool IsInstalled => _manager.IsInstalled;
    public bool IsPortable => _manager.IsInstalled && _manager.IsPortable;

    public string CurrentVersion =>
        _manager.CurrentVersion?.ToString()
        ?? typeof(AppUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "?";

    private static UpdateManager CreateManager(bool includePrerelease)
    {
        IUpdateSource source = Environment.GetEnvironmentVariable(SourceOverrideVariable) is { Length: > 0 } custom
            ? new SimpleFileSource(new DirectoryInfo(custom))
            : new GithubSource(RepositoryUrl, accessToken: null, prerelease: includePrerelease);
        return new UpdateManager(source);
    }

    /// <summary>Change de canal (stable seul, ou avec préversions) ; pris en compte à la prochaine recherche.</summary>
    public void SetIncludePrerelease(bool include)
    {
        if (include == _includePrerelease)
            return;
        _includePrerelease = include;
        _manager = CreateManager(include);
        if (State is AppUpdateState.UpToDate or AppUpdateState.Error)
            SetState(AppUpdateState.Idle);
    }

    /// <summary>Recherche une nouvelle version et la télécharge s'il y en a une.</summary>
    public async Task CheckAndDownloadAsync(CancellationToken ct = default)
    {
        var manager = _manager;   // le canal peut changer entre-temps : recherche et téléchargement avec le même
        if (State is AppUpdateState.Ready || !manager.IsInstalled)
            return;
        if (!await _gate.WaitAsync(0, ct))
            return;   // une recherche est déjà en cours
        try
        {
            Error = null;
            SetState(AppUpdateState.Checking);
            var update = await manager.CheckForUpdatesAsync().WaitAsync(ct);
            LastCheckUtc = DateTime.UtcNow;
            if (update is null)
            {
                SetState(AppUpdateState.UpToDate);
                return;
            }

            AvailableVersion = update.TargetFullRelease.Version.ToString();
            ReleaseNotes = update.TargetFullRelease.NotesMarkdown;
            Progress = 0;
            SetState(AppUpdateState.Downloading);
            await manager.DownloadUpdatesAsync(update, percent =>
            {
                Progress = percent;
                StateChanged?.Invoke();
            }, ct);
            _pending = update;
            SetState(AppUpdateState.Ready);
        }
        catch (OperationCanceledException)
        {
            SetState(AppUpdateState.Idle);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            LastCheckUtc = DateTime.UtcNow;
            SetState(AppUpdateState.Error);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Confie l'installation au programme de mise à jour, qui attend la fermeture de l'application
    /// (60 s au plus), puis la relance si <paramref name="restart"/>. L'appelant doit ensuite quitter.
    /// </summary>
    public bool ScheduleInstall(bool restart)
    {
        if (State != AppUpdateState.Ready)
            return false;
        try
        {
            // null : la dernière version téléchargée (y compris lors d'une session précédente)
            _manager.WaitExitThenApplyUpdates(_pending?.TargetFullRelease, silent: !restart, restart: restart);
            return true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            SetState(AppUpdateState.Error);
            return false;
        }
    }

    private void SetState(AppUpdateState state)
    {
        State = state;
        StateChanged?.Invoke();
    }
}
