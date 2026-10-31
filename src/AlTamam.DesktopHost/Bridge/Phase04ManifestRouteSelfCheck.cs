using System.Text.Json;
using AlTamam.Contracts.Bridge;
using Microsoft.Web.WebView2.Core;

namespace AlTamam.DesktopHost.Bridge;

/// <summary>
/// Windows-only actual browser route smoke: navigate EVERY canonical manifest
/// entry through the pinned local WebView2 virtual host, including the one
/// embedded AddPerson alias. Redirecting modules must eventually yield a
/// successful same-origin page; no external URL or screenshot can substitute.
/// The calling selfcheck owns the disposable synthetic profile and database.
/// </summary>
internal static class Phase04ManifestRouteSelfCheck
{
    public static async Task RunAsync(CoreWebView2 core, string webUiRoot)
    {
        ArgumentNullException.ThrowIfNull(core);
        ArgumentException.ThrowIfNullOrWhiteSpace(webUiRoot);
        string root = Path.GetFullPath(webUiRoot);
        using var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(root, "screen-manifest.json"))
                .ConfigureAwait(true));
        var screens = manifest.RootElement.GetProperty("screens");
        if (screens.GetArrayLength() != 69)
            throw new InvalidOperationException("Phase 04 must preserve the exact 69 route catalog");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var screen in screens.EnumerateArray())
        {
            string id = screen.GetProperty("screenId").GetString()
                ?? throw new InvalidOperationException("Route has no stable screen ID");
            string path = screen.GetProperty("localAssetPath").GetString()
                ?? throw new InvalidOperationException("Route has no local asset path");
            if (!ids.Add(id) || !path.StartsWith("screens/", StringComparison.Ordinal) ||
                !path.EndsWith("/index.html", StringComparison.Ordinal) ||
                path.Split('/').Any(part => part is ".." or "." or ""))
                throw new InvalidOperationException("Unsafe, duplicate or invalid canonical route: " + id);
            string candidate = Path.GetFullPath(Path.Combine(root,
                path.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
                throw new InvalidOperationException("Manifest route has no local HTML asset: " + id);

            string target = LocalWebUiOrigin.BaseUrl + path;
            var completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            bool targetStarted = false;
            EventHandler<CoreWebView2NavigationStartingEventArgs> onStart = (_, args) =>
            {
                if (string.Equals(args.Uri, target, StringComparison.OrdinalIgnoreCase))
                    targetStarted = true;
            };
            EventHandler<CoreWebView2NavigationCompletedEventArgs> onComplete = (_, args) =>
            {
                // A standalone HTML module may redirect to its authenticated
                // canonical owner. The original target still had to start.
                if (targetStarted && args.IsSuccess)
                    completion.TrySetResult(true);
            };
            core.NavigationStarting += onStart;
            core.NavigationCompleted += onComplete;
            try
            {
                core.Navigate(target);
                await completion.Task.WaitAsync(TimeSpan.FromSeconds(10))
                    .ConfigureAwait(true);
                if (!targetStarted)
                    throw new InvalidOperationException("Route was not navigated: " + id);
            }
            catch (TimeoutException ex)
            {
                throw new InvalidOperationException(
                    "Real WebView2 failed to open manifest route " + id + ": " + path, ex);
            }
            finally
            {
                core.NavigationStarting -= onStart;
                core.NavigationCompleted -= onComplete;
            }
        }
        if (ids.Count != 69 || ids.Contains("screen_020") ||
            !ids.Contains("screen_025") || !ids.Contains("screen_070") ||
            !ids.Contains("screen_002") || !ids.Contains("screen_069"))
            throw new InvalidOperationException("Phase 04 stable route ID invariant failed");
    }
}
