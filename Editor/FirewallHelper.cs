using System;
using System.Diagnostics;
using System.IO;
using UnityEditor;

namespace GamePeek
{
    /// <summary>
    /// Manages OS-level firewall rules required for the GamePeek WebSocket server.
    /// <para>
    /// On <b>Windows</b>: uses an elevated PowerShell script to:
    /// (1) remove any application-level block rules Windows auto-created when the
    ///     "Allow Unity through firewall?" prompt was dismissed, and
    /// (2) add a port-based allow rule for GamePeek.
    /// The result is persisted in <see cref="EditorPrefs"/> so setup only runs once.
    /// </para>
    /// <para>
    /// On <b>macOS / Linux</b>: no-op — the OS automatically prompts the user when
    /// a process first binds to a port.
    /// </para>
    /// </summary>
    public static class FirewallHelper
    {
        private const string PrefKey  = "GamePeek_FirewallConfigured";
        private const string RuleName = "GamePeek";

        /// <summary>
        /// Ensures an inbound firewall rule exists for <paramref name="port"/>.
        /// On Windows, runs an elevated PowerShell script the first time and persists
        /// the result in <see cref="EditorPrefs"/> so it never runs twice.
        /// </summary>
        /// <param name="port">TCP port to open (defaults to <see cref="GamePeekConstants.DefaultPort"/>).</param>
        public static void EnsureFirewallRule(int port = GamePeekConstants.DefaultPort)
        {
#if UNITY_EDITOR_WIN
            if (EditorPrefs.GetBool(PrefKey, false))
                return;

            AddWindowsFirewallRule(port);
#endif
        }

        /// <summary>
        /// Clears the stored flag and immediately re-runs firewall setup.
        /// Use this from the GamePeek window or the menu item below to test
        /// the setup flow on your own machine.
        /// </summary>
        public static void ResetAndReConfigure()
        {
            ResetFlag();
            EnsureFirewallRule(GamePeekConstants.DefaultPort);
        }

        /// <summary>
        /// Clears the stored flag so the rule will be re-evaluated on the next
        /// <see cref="EnsureFirewallRule"/> call. Useful after manually deleting the rule.
        /// </summary>
        public static void ResetFlag() => EditorPrefs.DeleteKey(PrefKey);

        /// <summary>
        /// Returns <c>true</c> when the firewall rule has already been successfully added.
        /// </summary>
        public static bool IsConfigured => EditorPrefs.GetBool(PrefKey, false);

#if UNITY_EDITOR_WIN
        private static void AddWindowsFirewallRule(int port)
        {
            try
            {
                // Get the currently-running Unity Editor executable path so we can
                // clear any app-level block rule Windows created when the "Allow Unity
                // through firewall?" prompt was previously dismissed or cancelled.
                string unityExe = Process.GetCurrentProcess().MainModule?.FileName
                                  ?? string.Empty;

                // Escape single quotes for PowerShell string literals.
                string safeExe  = unityExe.Replace("'", "''");

                // PowerShell script (written to a temp file to avoid cmd-line escaping issues):
                //   1. Remove any inbound block rules targeting this Unity executable.
                //   2. Remove stale GamePeek port rules (idempotent re-run safety).
                //   3. Add a fresh port-based allow rule covering all profiles.
                string script =
                    "# Step 1 – remove block rules Windows auto-created for Unity Editor\n" +
                    $"$exe = '{safeExe}'\n" +
                    "Get-NetFirewallRule -Direction Inbound -Action Block -ErrorAction SilentlyContinue | ForEach-Object {\n" +
                    "    $filter = $_ | Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue\n" +
                    "    if ($filter -and $filter.Program -eq $exe) { Remove-NetFirewallRule -Name $_.Name }\n" +
                    "}\n" +
                    $"# Step 2 – remove stale GamePeek rules\n" +
                    $"Remove-NetFirewallRule -DisplayName '{RuleName}' -ErrorAction SilentlyContinue\n" +
                    $"# Step 3 – add allow rule\n" +
                    $"New-NetFirewallRule -DisplayName '{RuleName}' -Direction Inbound " +
                    $"-Action Allow -Protocol TCP -LocalPort {port} -Profile Any | Out-Null\n";

                string tmpScript = Path.Combine(Path.GetTempPath(), "gamepeek_fw.ps1");
                File.WriteAllText(tmpScript, script);

                var psi = new ProcessStartInfo(
                    "powershell.exe",
                    $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{tmpScript}\"")
                {
                    UseShellExecute = true,
                    Verb            = "runas",   // one-time UAC elevation
                    WindowStyle     = ProcessWindowStyle.Hidden,
                    CreateNoWindow  = true,
                };

                using var proc = Process.Start(psi);
                proc?.WaitForExit(10000);

                try { File.Delete(tmpScript); } catch { /* best-effort cleanup */ }

                if (proc?.ExitCode != 0)
                {
                    GamePeekConstants.LogWarning(
                        $"[Firewall] Setup may not have completed (PowerShell exit {proc?.ExitCode}). " +
                        "UAC may have been denied. Will retry on next Start.");
                    return;
                }

                EditorPrefs.SetBool(PrefKey, true);
                GamePeekConstants.Log($"[Firewall] Rule '{RuleName}' configured for TCP {port} on all profiles.");
            }
            catch (Exception ex)
            {
                GamePeekConstants.LogWarning(
                    $"[Firewall] Could not configure automatically: {ex.Message}\n" +
                    "Run this in an elevated PowerShell:\n" +
                    $"  New-NetFirewallRule -DisplayName \"{RuleName}\" -Direction Inbound " +
                    $"-Action Allow -Protocol TCP -LocalPort {port} -Profile Any");
            }
        }
#endif
    }
}
