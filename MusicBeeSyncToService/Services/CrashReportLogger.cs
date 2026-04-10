using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace MusicBeePlugin.Services
{
    public static class CrashReportLogger
    {
        private static readonly object InitLock = new object();
        private static readonly object WriteLock = new object();
        private static readonly string CrashReportDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MusicBeeSyncToService");

        private static Func<IEnumerable<string>> recentLogProvider;
        private static Func<IDictionary<string, string>> stateProvider;
        private static Dispatcher attachedDispatcher;
        private static bool initialized;
        private static bool dispatcherHandlerAttached;
        private static string lastFingerprint;
        private static DateTime lastFingerprintUtc = DateTime.MinValue;
        private static string lastReportPath;

        public static string GetCrashReportDirectory()
        {
            return CrashReportDirectory;
        }

        public static void Initialize(
            Func<IEnumerable<string>> recentLogProviderFunc,
            Func<IDictionary<string, string>> stateProviderFunc,
            Dispatcher dispatcher = null)
        {
            if (recentLogProviderFunc != null)
            {
                recentLogProvider = recentLogProviderFunc;
            }

            if (stateProviderFunc != null)
            {
                stateProvider = stateProviderFunc;
            }

            lock (InitLock)
            {
                if (!initialized)
                {
                    AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
                    TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
                    initialized = true;
                }

                if (dispatcher != null && (!dispatcherHandlerAttached || !ReferenceEquals(attachedDispatcher, dispatcher)))
                {
                    if (attachedDispatcher != null && dispatcherHandlerAttached)
                    {
                        attachedDispatcher.UnhandledException -= Dispatcher_UnhandledException;
                    }

                    attachedDispatcher = dispatcher;
                    attachedDispatcher.UnhandledException += Dispatcher_UnhandledException;
                    dispatcherHandlerAttached = true;
                }
            }
        }

        public static string WriteExceptionReport(Exception ex, string context, IDictionary<string, string> additionalData = null, bool isUnhandled = false)
        {
            if (ex == null)
            {
                ex = new Exception("Unknown exception");
            }

            string fingerprint = BuildFingerprint(ex, context);

            lock (WriteLock)
            {
                if (!string.IsNullOrWhiteSpace(lastFingerprint)
                    && string.Equals(lastFingerprint, fingerprint, StringComparison.Ordinal)
                    && (DateTime.UtcNow - lastFingerprintUtc) < TimeSpan.FromSeconds(5)
                    && !string.IsNullOrWhiteSpace(lastReportPath))
                {
                    return lastReportPath;
                }

                Directory.CreateDirectory(CrashReportDirectory);

                string fileName = $"CrashReport_{DateTime.Now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid():N}.txt";
                string reportPath = Path.Combine(CrashReportDirectory, fileName);
                string reportText = BuildReportText(ex, context, additionalData, isUnhandled);

                File.WriteAllText(reportPath, reportText, Encoding.UTF8);

                lastFingerprint = fingerprint;
                lastFingerprintUtc = DateTime.UtcNow;
                lastReportPath = reportPath;

                return reportPath;
            }
        }

        private static void Dispatcher_UnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["UnhandledSource"] = "Dispatcher.UnhandledException",
                ["DispatcherThreadManagedId"] = System.Threading.Thread.CurrentThread.ManagedThreadId.ToString(CultureInfo.InvariantCulture),
                ["DispatcherMarkedHandled"] = e.Handled.ToString()
            };

            WriteExceptionReport(e.Exception, "DispatcherUnhandledException", details, true);
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            Exception ex = e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString() ?? "Unknown unhandled exception object");
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["UnhandledSource"] = "AppDomain.CurrentDomain.UnhandledException",
                ["IsTerminating"] = e.IsTerminating.ToString()
            };

            WriteExceptionReport(ex, "AppDomainUnhandledException", details, true);
        }

        private static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            var details = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["UnhandledSource"] = "TaskScheduler.UnobservedTaskException",
                ["ObservedBeforeHandler"] = e.Observed.ToString()
            };

            WriteExceptionReport(e.Exception, "TaskSchedulerUnobservedTaskException", details, true);
            e.SetObserved();
        }

        private static string BuildReportText(Exception ex, string context, IDictionary<string, string> additionalData, bool isUnhandled)
        {
            var sb = new StringBuilder(32768);

            sb.AppendLine("Music Bee Sync to Service Crash Report");
            sb.AppendLine(new string('=', 96));
            sb.AppendLine($"Generated Local Time : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff zzz}");
            sb.AppendLine($"Generated UTC Time   : {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z");
            sb.AppendLine($"Context              : {context ?? "(none)"}");
            sb.AppendLine($"Unhandled            : {isUnhandled}");
            sb.AppendLine($"Crash Report Folder  : {CrashReportDirectory}");
            sb.AppendLine();

            AppendDictionary(sb, "Process and Environment", BuildEnvironmentSnapshot());
            AppendDictionary(sb, "Plugin State Snapshot", MergeDictionaries(SafeReadStateProvider(), additionalData));
            AppendException(sb, ex);
            AppendRecentLogs(sb, SafeReadRecentLogs());
            AppendLoadedAssemblies(sb);

            return sb.ToString();
        }

        private static IDictionary<string, string> SafeReadStateProvider()
        {
            try
            {
                if (stateProvider == null)
                {
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                var providedState = stateProvider();
                return providedState != null
                    ? new Dictionary<string, string>(providedState, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["StateProviderError"] = ex.ToString()
                };
            }
        }

        private static IEnumerable<string> SafeReadRecentLogs()
        {
            try
            {
                return recentLogProvider != null
                    ? (recentLogProvider() ?? Enumerable.Empty<string>()).ToList()
                    : Enumerable.Empty<string>();
            }
            catch (Exception ex)
            {
                return new[] { "[CrashReportLogger] Failed to read recent log lines: " + ex };
            }
        }

        private static Dictionary<string, string> BuildEnvironmentSnapshot()
        {
            var processSnapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Process process = null;

            try
            {
                process = Process.GetCurrentProcess();
                processSnapshot["ProcessName"] = process.ProcessName;
                processSnapshot["ProcessId"] = process.Id.ToString(CultureInfo.InvariantCulture);
                processSnapshot["ProcessStartTime"] = SafeFormat(() => process.StartTime.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture));
                processSnapshot["MainWindowTitle"] = SafeFormat(() => process.MainWindowTitle);
                processSnapshot["WorkingSetMB"] = SafeFormat(() => (process.WorkingSet64 / 1024d / 1024d).ToString("F2", CultureInfo.InvariantCulture));
                processSnapshot["PrivateMemoryMB"] = SafeFormat(() => (process.PrivateMemorySize64 / 1024d / 1024d).ToString("F2", CultureInfo.InvariantCulture));
                processSnapshot["HandleCount"] = SafeFormat(() => process.HandleCount.ToString(CultureInfo.InvariantCulture));
                processSnapshot["ThreadCount"] = SafeFormat(() => process.Threads.Count.ToString(CultureInfo.InvariantCulture));
                processSnapshot["MainModuleFileName"] = SafeFormat(() => process.MainModule != null ? process.MainModule.FileName : string.Empty);
            }
            catch (Exception ex)
            {
                processSnapshot["ProcessSnapshotError"] = ex.ToString();
            }
            finally
            {
                if (process != null)
                {
                    process.Dispose();
                }
            }

            processSnapshot["CurrentManagedThreadId"] = System.Threading.Thread.CurrentThread.ManagedThreadId.ToString(CultureInfo.InvariantCulture);
            processSnapshot["CurrentThreadIsBackground"] = System.Threading.Thread.CurrentThread.IsBackground.ToString();
            processSnapshot["CurrentCulture"] = CultureInfo.CurrentCulture.Name;
            processSnapshot["CurrentUICulture"] = CultureInfo.CurrentUICulture.Name;
            processSnapshot["MachineName"] = Environment.MachineName;
            processSnapshot["UserName"] = Environment.UserName;
            processSnapshot["DomainName"] = Environment.UserDomainName;
            processSnapshot["OSVersion"] = Environment.OSVersion.ToString();
            processSnapshot["Is64BitOperatingSystem"] = Environment.Is64BitOperatingSystem.ToString();
            processSnapshot["Is64BitProcess"] = Environment.Is64BitProcess.ToString();
            processSnapshot["CLRVersion"] = Environment.Version.ToString();
            processSnapshot["SystemDirectory"] = SafeFormat(() => Environment.SystemDirectory);
            processSnapshot["CurrentDirectory"] = SafeFormat(() => Environment.CurrentDirectory);
            processSnapshot["CommandLine"] = SafeFormat(() => Environment.CommandLine);
            processSnapshot["AppDomainFriendlyName"] = SafeFormat(() => AppDomain.CurrentDomain.FriendlyName);
            processSnapshot["AppDomainBaseDirectory"] = SafeFormat(() => AppDomain.CurrentDomain.BaseDirectory);
            processSnapshot["ExecutingAssembly"] = SafeFormat(() => typeof(CrashReportLogger).Assembly.Location);
            processSnapshot["ExecutingAssemblyVersion"] = SafeFormat(() => typeof(CrashReportLogger).Assembly.GetName().Version != null ? typeof(CrashReportLogger).Assembly.GetName().Version.ToString() : string.Empty);

            return processSnapshot;
        }

        private static void AppendException(StringBuilder sb, Exception ex)
        {
            sb.AppendLine("Exception Details");
            sb.AppendLine(new string('-', 96));
            AppendExceptionRecursive(sb, ex, 0);
            sb.AppendLine();
        }

        private static void AppendExceptionRecursive(StringBuilder sb, Exception ex, int depth)
        {
            if (ex == null)
            {
                sb.AppendLine("(null exception)");
                return;
            }

            string indent = new string(' ', depth * 2);
            sb.AppendLine(indent + "Type       : " + ex.GetType().FullName);
            sb.AppendLine(indent + "Message    : " + ex.Message);
            sb.AppendLine(indent + "Source     : " + (ex.Source ?? string.Empty));
            sb.AppendLine(indent + "TargetSite : " + (ex.TargetSite != null ? ex.TargetSite.ToString() : string.Empty));
            sb.AppendLine(indent + "HResult    : 0x" + ex.HResult.ToString("X8", CultureInfo.InvariantCulture));

            if (ex.Data != null && ex.Data.Count > 0)
            {
                sb.AppendLine(indent + "Data:");
                foreach (DictionaryEntry item in ex.Data)
                {
                    sb.AppendLine(indent + "  - " + (item.Key ?? "(null)") + " = " + (item.Value ?? "(null)"));
                }
            }

            if (!string.IsNullOrWhiteSpace(ex.StackTrace))
            {
                sb.AppendLine(indent + "StackTrace:");
                foreach (var line in SplitLines(ex.StackTrace))
                {
                    sb.AppendLine(indent + "  " + line);
                }
            }

            if (ex is ReflectionTypeLoadException reflectionTypeLoadException && reflectionTypeLoadException.LoaderExceptions != null)
            {
                int index = 0;
                foreach (var loaderException in reflectionTypeLoadException.LoaderExceptions)
                {
                    sb.AppendLine(indent + $"LoaderException[{index}]:");
                    AppendExceptionRecursive(sb, loaderException, depth + 1);
                    index++;
                }
            }

            if (ex is AggregateException aggregateException)
            {
                int index = 0;
                foreach (var inner in aggregateException.Flatten().InnerExceptions)
                {
                    sb.AppendLine(indent + $"InnerException[{index}]:");
                    AppendExceptionRecursive(sb, inner, depth + 1);
                    index++;
                }
            }
            else if (ex.InnerException != null)
            {
                sb.AppendLine(indent + "InnerException:");
                AppendExceptionRecursive(sb, ex.InnerException, depth + 1);
            }

            sb.AppendLine(indent + "ToString():");
            foreach (var line in SplitLines(ex.ToString()))
            {
                sb.AppendLine(indent + "  " + line);
            }
        }

        private static void AppendRecentLogs(StringBuilder sb, IEnumerable<string> lines)
        {
            sb.AppendLine("Recent Log Lines");
            sb.AppendLine(new string('-', 96));

            var materialized = (lines ?? Enumerable.Empty<string>()).ToList();
            if (materialized.Count == 0)
            {
                sb.AppendLine("(no recent log lines available)");
                sb.AppendLine();
                return;
            }

            foreach (var line in materialized)
            {
                sb.AppendLine(line ?? string.Empty);
            }

            sb.AppendLine();
        }

        private static void AppendLoadedAssemblies(StringBuilder sb)
        {
            sb.AppendLine("Loaded Assemblies");
            sb.AppendLine(new string('-', 96));

            try
            {
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().OrderBy(a => a.GetName().Name, StringComparer.OrdinalIgnoreCase))
                {
                    string location = string.Empty;
                    try
                    {
                        location = assembly.IsDynamic ? "(dynamic)" : assembly.Location;
                    }
                    catch (Exception ex)
                    {
                        location = "(location unavailable: " + ex.Message + ")";
                    }

                    string version = assembly.GetName().Version != null ? assembly.GetName().Version.ToString() : string.Empty;
                    sb.AppendLine($"{assembly.GetName().Name} | Version={version} | Location={location}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("Failed to enumerate loaded assemblies: " + ex);
            }

            sb.AppendLine();
        }

        private static void AppendDictionary(StringBuilder sb, string title, IDictionary<string, string> values)
        {
            sb.AppendLine(title);
            sb.AppendLine(new string('-', 96));

            if (values == null || values.Count == 0)
            {
                sb.AppendLine("(none)");
                sb.AppendLine();
                return;
            }

            foreach (var item in values.OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"{item.Key} = {item.Value}");
            }

            sb.AppendLine();
        }

        private static IDictionary<string, string> MergeDictionaries(IDictionary<string, string> primary, IDictionary<string, string> secondary)
        {
            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (primary != null)
            {
                foreach (var item in primary)
                {
                    merged[item.Key] = item.Value ?? string.Empty;
                }
            }

            if (secondary != null)
            {
                foreach (var item in secondary)
                {
                    merged[item.Key] = item.Value ?? string.Empty;
                }
            }

            return merged;
        }

        private static string BuildFingerprint(Exception ex, string context)
        {
            var sb = new StringBuilder();
            sb.Append(context ?? string.Empty);
            sb.Append('|');
            sb.Append(ex.GetType().FullName);
            sb.Append('|');
            sb.Append(ex.Message ?? string.Empty);
            sb.Append('|');

            var stackLines = SplitLines(ex.StackTrace).Take(8);
            foreach (var line in stackLines)
            {
                sb.Append(line);
                sb.Append('|');
            }

            return sb.ToString();
        }

        private static IEnumerable<string> SplitLines(string text)
        {
            return string.IsNullOrEmpty(text)
                ? Enumerable.Empty<string>()
                : text.Replace("\r\n", "\n").Split(new[] { '\n' }, StringSplitOptions.None);
        }

        private static string SafeFormat(Func<string> valueFactory)
        {
            try
            {
                return valueFactory() ?? string.Empty;
            }
            catch (Exception ex)
            {
                return "(unavailable: " + ex.Message + ")";
            }
        }
    }
}
