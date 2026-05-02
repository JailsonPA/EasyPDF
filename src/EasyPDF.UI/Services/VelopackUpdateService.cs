using EasyPDF.Application.Interfaces;
using Velopack;
using Velopack.Sources;
using AppUpdateInfo = EasyPDF.Application.Interfaces.UpdateInfo;

namespace EasyPDF.UI.Services;

internal sealed class VelopackUpdateService : IUpdateService
{
    private readonly string _owner;
    private readonly string _repo;
    private readonly UpdateManager _mgr;
    private Velopack.UpdateInfo? _pendingUpdate;

    public VelopackUpdateService(string owner, string repo)
    {
        _owner = owner;
        _repo  = repo;
        var source = new GithubSource($"https://github.com/{owner}/{repo}", null, false);
        _mgr = new UpdateManager(source);
    }

    public bool CanInstall => _mgr.IsInstalled;

    public async Task<AppUpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        if (!_mgr.IsInstalled) return null;
        try
        {
            var info = await _mgr.CheckForUpdatesAsync();
            if (info is null) return null;
            _pendingUpdate = info;
            string version = info.TargetFullRelease.Version.ToString();
            string url     = $"https://github.com/{_owner}/{_repo}/releases/tag/v{version}";
            return new AppUpdateInfo(version, url);
        }
        catch
        {
            return null;
        }
    }

    public async Task DownloadUpdateAsync(AppUpdateInfo update, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (_pendingUpdate is null) return;
        await _mgr.DownloadUpdatesAsync(_pendingUpdate, p => progress?.Report(p));
    }

    public void ApplyUpdateAndRestart()
    {
        if (_pendingUpdate is null) return;
        _mgr.ApplyUpdatesAndRestart(_pendingUpdate);
    }
}
