﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Squirrel.SimpleSplat;
using Squirrel.Sources;
using NuGet.Versioning;
using System.Runtime.Versioning;

namespace Squirrel
{
    /// <inheritdoc cref="IUpdateManager"/>
    public partial class UpdateManager : IUpdateManager
    {
        /// <inheritdoc/>
        public bool IsInstalledApp => CurrentlyInstalledVersion() != null;

        /// <summary>The <see cref="UpdateConfig"/> describes the structure of the application on disk (eg. file/folder locations).</summary>
        public UpdateConfig Config => _config;

        /// <summary>The <see cref="IUpdateSource"/> responsible for retrieving updates from a package repository.</summary>
        public IUpdateSource Source => _source;

        private readonly IUpdateSource _source;
        private readonly UpdateConfig _config;
        private readonly object _lockobj = new object();
        private IDisposable _updateLock;
        private bool _disposed;

        /// <summary>
        /// Create a new instance of <see cref="UpdateManager"/> to check for and install updates. 
        /// Do not forget to dispose this class! This constructor is just a shortcut for
        /// <see cref="UpdateManager(IUpdateSource, string, string)"/>, and will automatically create
        /// a <see cref="SimpleFileSource"/> or a <see cref="SimpleWebSource"/> depending on 
        /// whether 'urlOrPath' is a filepath or a URL, respectively.
        /// </summary>
        /// <param name="urlOrPath">
        /// The URL where your update packages or stored, or a local package repository directory.
        /// </param>
        /// <param name="applicationIdOverride">
        /// The Id of your application should correspond with the 
        /// appdata directory name, and the Id used with Squirrel releasify/pack.
        /// If left null/empty, UpdateManger will attempt to determine the current application Id  
        /// from the installed app location, or throw if the app is not currently installed during certain 
        /// operations.
        /// </param>
        /// <param name="localAppDataDirectoryOverride">
        /// Provide a custom location for the system LocalAppData, it will be used 
        /// instead of <see cref="Environment.SpecialFolder.LocalApplicationData"/>.
        /// </param>
        /// <param name="urlDownloader">
        /// A custom file downloader, for using non-standard package sources or adding proxy configurations. 
        /// </param>
        public UpdateManager(
            string urlOrPath,
            string applicationIdOverride = null,
            string localAppDataDirectoryOverride = null,
            IFileDownloader urlDownloader = null)
            : this(CreateSource(urlOrPath, urlDownloader), applicationIdOverride, localAppDataDirectoryOverride)
        { }

        /// <summary>
        /// Create a new instance of <see cref="UpdateManager"/> to check for and install updates. 
        /// Do not forget to dispose this class!
        /// </summary>
        /// <param name="updateSource">
        /// The source of your update packages. This can be a web server (<see cref="SimpleWebSource"/>),
        /// a local directory (<see cref="SimpleFileSource"/>), a GitHub repository (<see cref="GithubSource"/>),
        /// or a custom location.
        /// </param>
        /// <param name="applicationIdOverride">
        /// The Id of your application should correspond with the 
        /// appdata directory name, and the Id used with Squirrel releasify/pack.
        /// If left null/empty, UpdateManger will attempt to determine the current application Id  
        /// from the installed app location, or throw if the app is not currently installed during certain 
        /// operations.
        /// </param>
        /// <param name="localAppDataDirectoryOverride">
        /// Provide a custom location for the system LocalAppData, it will be used 
        /// instead of <see cref="Environment.SpecialFolder.LocalApplicationData"/>.
        /// </param>
        public UpdateManager(
            IUpdateSource updateSource,
            string applicationIdOverride = null,
            string localAppDataDirectoryOverride = null)
            : this(updateSource, new UpdateConfig(applicationIdOverride, localAppDataDirectoryOverride))
        { }

        public UpdateManager(
            string urlOrPath,
            UpdateConfig config,
            IFileDownloader urlDownloader = null)
            : this(CreateSource(urlOrPath, urlDownloader), config)
        { }

        public UpdateManager(IUpdateSource source, UpdateConfig config)
        {
            _source = source;
            _config = config;
        }

        internal UpdateManager() { }

        /// <summary>Clean up UpdateManager resources</summary>
        ~UpdateManager()
        {
            Dispose();
        }

        /// <inheritdoc/>
        public async Task<string> ApplyReleases(UpdateInfo updateInfo, Action<int> progress = null)
        {
            return await ApplyReleases(updateInfo, false, false, progress).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        [SupportedOSPlatform("windows")]
        public async Task FullInstall(bool silentInstall = false, Action<int> progress = null)
        {
            var updateInfo = await CheckForUpdate(intention: UpdaterIntention.Install).ConfigureAwait(false);
            await DownloadReleases(updateInfo.ReleasesToApply).ConfigureAwait(false);
            await ApplyReleases(updateInfo, silentInstall, true, progress).ConfigureAwait(false);
        }

        /// <inheritdoc/>
        public SemanticVersion CurrentlyInstalledVersion()
        {
            return _config.CurrentlyInstalledVersion;
        }

        /// <inheritdoc/>
        public async Task<ReleaseEntry> UpdateApp(Action<int> progress = null)
        {
            progress = progress ?? (_ => { });
            this.Log().Info("Starting automatic update");

            bool ignoreDeltaUpdates = false;

        retry:
            var updateInfo = default(UpdateInfo);

            try {
                var localVersions = _config.GetVersions();
                var currentVersion = CurrentlyInstalledVersion();

                updateInfo = await this.ErrorIfThrows(() => CheckForUpdate(ignoreDeltaUpdates, x => progress(x / 3)),
                    "Failed to check for updates").ConfigureAwait(false);

                if (updateInfo == null || updateInfo.FutureReleaseEntry == null) {
                    this.Log().Info("No update available.");
                    return null;
                }

                if (currentVersion >= updateInfo.FutureReleaseEntry.Version) {
                    this.Log().Info($"Current version {currentVersion} is up to date with remote.");
                    return null;
                }

                if (localVersions.Any(v => v.Version == updateInfo.FutureReleaseEntry.Version)) {
                    this.Log().Info("Update available, it is already downloaded.");
                    return updateInfo.FutureReleaseEntry;
                }

                await this.ErrorIfThrows(() =>
                    DownloadReleases(updateInfo.ReleasesToApply, x => progress(x / 3 + 33)),
                    "Failed to download updates").ConfigureAwait(false);

                await this.ErrorIfThrows(() =>
                    ApplyReleases(updateInfo, x => progress(x / 3 + 66)),
                    "Failed to apply updates").ConfigureAwait(false);

                await this.ErrorIfThrows(() =>
                    CreateUninstallerRegistryEntry(),
                    "Failed to set up uninstaller").ConfigureAwait(false);
            } catch {
                if (ignoreDeltaUpdates == false) {
                    ignoreDeltaUpdates = true;
                    goto retry;
                }

                throw;
            }

            return updateInfo.ReleasesToApply.Any() ?
                updateInfo.ReleasesToApply.MaxBy(x => x.Version).Last() :
                default(ReleaseEntry);
        }

        /// <inheritdoc/>
        public void KillAllExecutablesBelongingToPackage()
        {
            Utility.KillProcessesInDirectory(_config.RootAppDir);
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            lock (_lockobj) {
                var disp = Interlocked.Exchange(ref _updateLock, null);
                if (disp != null) {
                    disp.Dispose();
                }
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }

        /// <summary>
        /// Terminates the current process immediately (with <see cref="Environment.Exit"/>) and 
        /// re-launches the latest version of the current (or target) executable. 
        /// </summary>
        /// <param name="exeToStart">The file *name* (not full path) of the exe to start, or null to re-launch 
        /// the current executable. </param>
        /// <param name="arguments">Arguments to start the exe with</param>
        /// <remarks>See <see cref="RestartAppWhenExited(string, string)"/> for a version which does not
        /// exit the current process immediately, but instead allows you to exit the current process
        /// however you'd like.</remarks>
        public void RestartApp(string exeToStart = null, string arguments = null)
        {
            restartProcess(exeToStart, arguments);
            // NB: We have to give update.exe some time to grab our PID
            Thread.Sleep(500);
            Environment.Exit(0);
        }

        /// <summary>
        /// Launch Update.exe and ask it to wait until this process exits before starting
        /// a new process. Used to re-start your app with the latest version after an update.
        /// </summary>
        /// <param name="exeToStart">The file *name* (not full path) of the exe to start, or null to re-launch 
        /// the current executable. </param>
        /// <param name="arguments">Arguments to start the exe with</param>
        /// <returns>The Update.exe process that is waiting for this process to exit</returns>
        public Process RestartAppWhenExited(string exeToStart = null, string arguments = null)
        {
            var process = restartProcess(exeToStart, arguments);
            // NB: We have to give update.exe some time to grab our PID
            Thread.Sleep(500);
            return process;
        }

        /// <summary>
        /// Launch Update.exe and ask it to wait until this process exits before starting
        /// a new process. Used to re-start your app with the latest version after an update.
        /// </summary>
        /// <param name="exeToStart">The file *name* (not full path) of the exe to start, or null to re-launch 
        /// the current executable. </param>
        /// <param name="arguments">Arguments to start the exe with</param>
        /// <returns>The Update.exe process that is waiting for this process to exit</returns>
        public async Task<Process> RestartAppWhenExitedAsync(string exeToStart = null, string arguments = null)
        {
            var process = restartProcess(exeToStart, arguments);
            // NB: We have to give update.exe some time to grab our PID
            await Task.Delay(500).ConfigureAwait(false);
            return process;
        }

        private Process restartProcess(string exeToStart = null, string arguments = null)
        {
            // NB: Here's how this method works:
            //
            // 1. We're going to pass the *name* of our EXE and the params to 
            //    Update.exe
            // 2. Update.exe is going to grab our PID (via getting its parent), 
            //    then wait for us to exit.
            // 3. Return control and new Process back to caller and allow them to Exit as desired.
            // 4. After our process exits, Update.exe unblocks, then we launch the app again, possibly 
            //    launching a different version than we started with (this is why
            //    we take the app's *name* rather than a full path)

            exeToStart = exeToStart ?? Path.GetFileName(SquirrelRuntimeInfo.EntryExePath);

            List<string> args = new() {
                "--forceLatest",
                "--processStartAndWait",
                exeToStart,
            };

            if (arguments != null) {
                args.Add("-a");
                args.Add(arguments);
            }

            return Process.Start(_config.UpdateExePath, Utility.ArgsToCommandLine(args));
        }

        private static string GetLocalAppDataDirectory(string assemblyLocation = null)
        {
            // if we're installed and running as update.exe in the app folder, the app directory root is one folder up
            if (SquirrelRuntimeInfo.IsSingleFile && Path.GetFileName(SquirrelRuntimeInfo.EntryExePath).Equals("Update.exe", StringComparison.OrdinalIgnoreCase)) {
                var oneFolderUpFromAppFolder = Path.Combine(Path.GetDirectoryName(SquirrelRuntimeInfo.EntryExePath), "..");
                return Path.GetFullPath(oneFolderUpFromAppFolder);
            }

            // if update exists above us, we're running from within a version directory, and the appdata folder is two above us
            if (File.Exists(Path.Combine(SquirrelRuntimeInfo.BaseDirectory, "..", "Update.exe"))) {
                var twoFoldersUpFromAppFolder = Path.Combine(Path.GetDirectoryName(SquirrelRuntimeInfo.EntryExePath), "..\\..");
                return Path.GetFullPath(twoFoldersUpFromAppFolder);
            }

            // if neither of the above are true, we're probably not installed yet, so return the real appdata directory
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        private static IUpdateSource CreateSource(string urlOrPath, IFileDownloader urlDownloader)
        {
            if (String.IsNullOrWhiteSpace(urlOrPath)) {
                return null;
            }

            if (Utility.IsHttpUrl(urlOrPath)) {
                return new SimpleWebSource(urlOrPath, urlDownloader ?? Utility.CreateDefaultDownloader());
            } else {
                return new SimpleFileSource(new DirectoryInfo(urlOrPath));
            }
        }

        private Task<IDisposable> acquireUpdateLock()
        {
            lock (_lockobj) {
                if (_disposed) throw new ObjectDisposedException(nameof(UpdateManager));
                if (_updateLock != null) return Task.FromResult(_updateLock);
            }

            return Task.Run(() => {
                var key = Utility.CalculateStreamSHA1(new MemoryStream(Encoding.UTF8.GetBytes(_config.RootAppDir)));

                IDisposable theLock;
                try {
                    theLock = ModeDetector.InUnitTestRunner() ?
                        Disposable.Create(() => { }) : new SingleGlobalInstance(key, TimeSpan.FromMilliseconds(2000));
                } catch (TimeoutException) {
                    throw new TimeoutException("Couldn't acquire update lock, another instance may be running updates");
                }

                var ret = Disposable.Create(() => {
                    theLock.Dispose();
                    _updateLock = null;
                });

                _updateLock = ret;
                return ret;
            });
        }

        /// <summary>
        /// Calculates the total percentage of a specific step that should report within a specific range.
        /// <para />
        /// If a step needs to report between 50 -> 75 %, this method should be used as CalculateProgress(percentage, 50, 75). 
        /// </summary>
        /// <param name="percentageOfCurrentStep">The percentage of the current step, a value between 0 and 100.</param>
        /// <param name="stepStartPercentage">The start percentage of the range the current step represents.</param>
        /// <param name="stepEndPercentage">The end percentage of the range the current step represents.</param>
        /// <returns>The calculated percentage that can be reported about the total progress.</returns>
        internal static int CalculateProgress(int percentageOfCurrentStep, int stepStartPercentage, int stepEndPercentage)
        {
            // Ensure we are between 0 and 100
            percentageOfCurrentStep = Math.Max(Math.Min(percentageOfCurrentStep, 100), 0);

            var range = stepEndPercentage - stepStartPercentage;
            var singleValue = range / 100d;
            var totalPercentage = (singleValue * percentageOfCurrentStep) + stepStartPercentage;

            return (int) totalPercentage;
        }
    }
}
