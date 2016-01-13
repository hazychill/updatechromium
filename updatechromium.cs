using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using Hazychill.Setting;

public static class Program {
  private static object consoleWriteLock = new object();
  private static object httpClientSetupLock = new object();

  private static ConcurrentDictionary<Exception, Object> errorMemo;

  private const string MUTEX_NAME = "FE904DE5-11D9-420A-E54D-90FED9110A42";

  private const int MAX_PASSWORD_LENGTH = 100;

  private const string SMNGKEY_BASE_DIR = "baseDir";
  private const string SMNGKEY_UNZIP = "unzip";
  private const string SMNGKEY_HASH_URL = "hashUrl";
  private const string SMNGKEY_ZIP_URL_TEMPLATE = "zipUrlTemplate";
  private const string SMNGKEY_EXE_NAME = "exeName";
  private const string SMNGKEY_SLEEP_SEC = "sleepSec";
  private const string SMNGKEY_SUSPENDED_FILE_PATH = "suspendFileName";
  private const string SMNGKEY_PROXY_HOST = "proxyHost";
  private const string SMNGKEY_PROXY_PORT = "proxyPort";
  private const string SMNGKEY_PROXY_USER = "proxyUser";
  private const string SMNGKEY_PROXY_PASSWORD = "proxyPassword";
  private const string SMNGKEY_BACKUP_CYCLE = "backupCycle";

  private const string SMNGKEY_IS_SUSPENDED = "isSuspended";
  private const string SMNGKEY_IS_LATEST = "isLatest";
  private const string SMNGKEY_CANCELLATION_TOKEN = "cancellationToken";
  private const string SMNGKEY_CANCELLATION_TOKEN_SOURCE = "cancellationTokenSource";
  private const string SMNGKEY_SHOULD_WAIT = "shouldWait";
  private const string SMNGKEY_REVISION = "revision";
  private const string SMNGKEY_HTTP_CLIENT = "httpClient";
  private const string SMNGKEY_DOWNLOAD_PATH = "downloadPath";
  private const string SMNGKEY_UNZIPPED_DIR = "unzippedDir";

  static Program() {
    errorMemo = new ConcurrentDictionary<Exception, object>();
  }

  public static void Main() {
    Mutex mutex = null;
    var shouldWait = true;
    try {
      bool createdNew;
      mutex = new Mutex(true, MUTEX_NAME, out createdNew);

      if (createdNew == false) {
        OutputMessage("updatechromium is already running.");
        return;
      }

      shouldWait = Exec();
    }
    catch (Exception e) {
      OutputError(e);
    }
    finally {
      if (shouldWait) {
        while (Console.KeyAvailable) Console.ReadKey();
        OutputMessage("");
        OutputMessage("Press enter to exit");
        Console.ReadLine();
      }

      if (mutex != null) {
        try {
          mutex.ReleaseMutex();
        }
        catch (Exception e1) {
          OutputError(e1);
        }
        try {
          mutex.Dispose();
        }
        catch (Exception e1) {
          OutputError(e1);
        }
      }
    }
  }

  private static bool Exec() {
    var smng = new SettingsManager();
    LoadSettings(smng);

    var cts = new CancellationTokenSource();
    var cancellationToken = cts.Token;
    smng.SetOrAddNewItem(SMNGKEY_CANCELLATION_TOKEN_SOURCE, cts);
    smng.SetOrAddNewItem(SMNGKEY_CANCELLATION_TOKEN, cancellationToken);

    Console.CancelKeyPress += (sender, e) => {
      if (e.SpecialKey == ConsoleSpecialKey.ControlC) {
        cts.Cancel();
        e.Cancel = true;
      }
    };

    var mreCheckSuspendedFinished = new ManualResetEventSlim();
    var checkSuspendedTask = Task.Factory.StartNew(() => {
      cancellationToken.ThrowIfCancellationRequested();
      CheckSuspended(smng);
      var isSuspended = smng.GetItem<bool>(SMNGKEY_IS_SUSPENDED);
      if (isSuspended) {
        OutputMessage("updatechromium suspended.");
        smng.SetOrAddNewItem(SMNGKEY_SHOULD_WAIT, false);
        cts.Cancel();
      }
    }, cancellationToken);
    checkSuspendedTask.ContinueWith(task => {
      if (task.IsFaulted) {
        OutputError(task.Exception);
        cts.Cancel();
      }
      mreCheckSuspendedFinished.Set();
    });

    var mreGetRevisionFinished = new ManualResetEventSlim();
    var getRevisionTask = Task.Factory.StartNew(() => {
      mreCheckSuspendedFinished.Wait(cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      GetRevision(smng);
      var isLatest = smng.GetItem<bool>(SMNGKEY_IS_LATEST);
      if (isLatest) {
        OutputMessage("Latest.");
        smng.SetOrAddNewItem(SMNGKEY_SHOULD_WAIT, false);
        cts.Cancel();
      }
    }, cancellationToken);
    getRevisionTask.ContinueWith(task => {
      if (task.IsFaulted) {
        OutputError(task.Exception);
        cts.Cancel();
      }
      mreGetRevisionFinished.Set();
    });

    var mreZipDownloadFinished = new ManualResetEventSlim();
    var downloadTask = Task.Factory.StartNew(() => {
      mreGetRevisionFinished.Wait(cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      DownloadZip(smng);
    }, cancellationToken);
    downloadTask.ContinueWith(task => {
      if (task.IsFaulted) {
        OutputError(task.Exception);
        cts.Cancel();
      }
      mreZipDownloadFinished.Set();
    });

    var mreUnzipFinished = new ManualResetEventSlim();
    var unzipTask = Task.Factory.StartNew(() => {
      mreZipDownloadFinished.Wait(cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      Unzip(smng);
    }, cancellationToken);
    unzipTask.ContinueWith(task => {
      if (task.IsFaulted) {
        OutputError(task.Exception);
        cts.Cancel();
      }
      mreUnzipFinished.Set();
    });

    var mreChromiumExited = new ManualResetEventSlim();
    var waitChromiumExitTask = Task.Factory.StartNew(() => {
      mreGetRevisionFinished.Wait(cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      WaitChromiumExited(smng);
    }, cancellationToken);
    waitChromiumExitTask.ContinueWith(task => {
      if (task.IsFaulted) {
        OutputError(task.Exception);
        cts.Cancel();
      }
      mreChromiumExited.Set();
    });

    var mreBackupExeDirFinished = new ManualResetEventSlim();
    var backupExeDirTask = Task.Factory.StartNew(() => {
      mreChromiumExited.Wait(cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      BackupExeDir(smng);
    }, cancellationToken);
    backupExeDirTask.ContinueWith(task => {
      if (task.IsFaulted) {
        OutputError(task.Exception);
        cts.Cancel();
      }
      mreBackupExeDirFinished.Set();
    });

    var mreBackupProfileDirFinished = new ManualResetEventSlim();
    var backupProfileDirTask = Task.Factory.StartNew(() => {
      mreChromiumExited.Wait(cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      BackupProfileDir(smng);
    }, cancellationToken);
    backupProfileDirTask.ContinueWith(task => {
      if (task.IsFaulted) {
        OutputError(task.Exception);
        cts.Cancel();
      }
      mreBackupProfileDirFinished.Set();
    });

    var renameUnzipedTask = Task.Factory.StartNew(() => {
      mreBackupExeDirFinished.Wait(cancellationToken);
      mreBackupProfileDirFinished.Wait(cancellationToken);
      mreUnzipFinished.Wait(cancellationToken);
      cancellationToken.ThrowIfCancellationRequested();
      RenameUnziped(smng);
    }, cancellationToken);

    try {
      renameUnzipedTask.Wait(cancellationToken);
    }
    catch (OperationCanceledException e) {
      lock (consoleWriteLock) {
        Debug.WriteLine(e);
      }
    }

    bool shouldWait;
    if (smng.TryGetItem(SMNGKEY_SHOULD_WAIT, out shouldWait)) {
      return shouldWait;
    }
    else {
      return true;
    }
  }

  private static void LoadSettings(SettingsManager smng) {
    OutputDebug("LoadSettings start");
    string execDir = GetExecDir();
    string settingsFilePath = Path.Combine(execDir, "settings.txt");
    smng.Load(settingsFilePath);
    OutputDebug("LoadSettings end");
  }

  private static string GetExecDir() {
    OutputDebug("GetExecDir start");
    Assembly myAssembly = Assembly.GetEntryAssembly();
    string path = myAssembly.Location;

    OutputDebug("GetExecDir end");
    return Path.GetDirectoryName(path);
  }

  private static void CheckSuspended(ISettingsManager smng) {
    OutputDebug("CheckSuspended start");
    bool isSuspended;
    string suspendFileName;
    if (smng.TryGetItem(SMNGKEY_SUSPENDED_FILE_PATH, out suspendFileName)) {
      string execDir = GetExecDir();
      string suspendFilePath = Path.Combine(execDir, suspendFileName);
      isSuspended = File.Exists(suspendFilePath);
    }
    else {
      isSuspended = false;
    }

    smng.SetOrAddNewItem(SMNGKEY_IS_SUSPENDED, isSuspended);
    OutputDebug("CheckSuspended end");
  }

  private static void GetRevision(ISettingsManager smng) {
    OutputDebug("GetRevision start");
    var cancellationToken = smng.GetItem<CancellationToken>(SMNGKEY_CANCELLATION_TOKEN).Value;
    var client = GetHttpClient(smng);
    var hashUrl = smng.GetItem<Uri>(SMNGKEY_HASH_URL);
    var revision = client.GetAsync(hashUrl, cancellationToken).Result.Content.ReadAsStringAsync().Result;

    var baseDir = smng.GetItem<string>(SMNGKEY_BASE_DIR);
    var pattern = string.Format("chrome-win32_*_{0}.zip", revision);
    bool isLatest;
    if (Directory.EnumerateFiles(baseDir, pattern).Any()) {
      isLatest = true;
    }
    else {
      isLatest = false;
    }
    smng.SetOrAddNewItem(SMNGKEY_IS_LATEST, isLatest);
    smng.SetOrAddNewItem(SMNGKEY_REVISION, revision);
    OutputDebug("GetRevision end");
  }

  private static HttpClient GetHttpClient(ISettingsManager smng) {
    OutputDebug("GetHttpClient start");
    HttpClient client;
    lock (httpClientSetupLock) {
      if (!smng.TryGetItem(SMNGKEY_HTTP_CLIENT, out client)) {
        var clientHandler = new HttpClientHandler();
        var proxy = GetProxySettings(smng);
        clientHandler.Proxy = proxy;
        client = new HttpClient(clientHandler);
        smng.SetOrAddNewItem(SMNGKEY_HTTP_CLIENT, client);
      }
    }

    OutputDebug("GetHttpClient end");
    return client;
  }

  private static WebProxy GetProxySettings(ISettingsManager smng) {
    OutputDebug("GetProxySettings start");
    string proxyHost;
    if (smng.TryGetItem(SMNGKEY_PROXY_HOST, out proxyHost)) {
      int proxyPort;
      if (!smng.TryGetItem(SMNGKEY_PROXY_PORT, out proxyPort)) {
        proxyPort = 8080;
      }
      var proxy = new WebProxy(proxyHost, proxyPort);
      string proxyUser;
      if (smng.TryGetItem(SMNGKEY_PROXY_USER, out proxyUser)) {
        string proxyPassword;
        if (!smng.TryGetItem(SMNGKEY_PROXY_PASSWORD, out proxyPassword)) {
          proxyPassword = ReceivePassword();
          smng.SetOrAddNewItem(SMNGKEY_PROXY_PASSWORD, proxyPassword);
        }
        var credential = new NetworkCredential(proxyUser, proxyPassword);
        proxy.Credentials = credential;
      }
      OutputDebug("GetProxySettings end");
      return proxy;
    }
    else {
      OutputDebug("GetProxySettings end");
      return null;
    }
  }

  private static string ReceivePassword() {
    OutputDebug("ReceivePassword start");
    var buffer = new StringBuilder();
    var inputEnd = false;
    Console.Write("Proxy password: ");
    while (inputEnd == false) {
      var c = Console.ReadKey(true);
      if (c.Key == ConsoleKey.Enter) {
        inputEnd = true;
      }
      else if (c.Key == ConsoleKey.Backspace) {
        if (buffer.Length >= 1) {
          buffer.Remove(buffer.Length-1, 1);
        }
      }
      else if (c.KeyChar != '\u0000') {
        buffer.Append(c.KeyChar);
      }
    }
    OutputDebug("ReceivePassword end");
    return buffer.ToString();
  }

  private static void DownloadZip(ISettingsManager smng) {
    OutputDebug("DownloadZip start");
    var cancellationToken = smng.GetItem<CancellationToken>(SMNGKEY_CANCELLATION_TOKEN).Value;
    var revision = smng.GetItem<string>(SMNGKEY_REVISION);
    string zipUrlTemplate = smng.GetItem<string>(SMNGKEY_ZIP_URL_TEMPLATE);
    var zipUrlStr = zipUrlTemplate.Replace("{hash}", revision);
    var zipUrl = new Uri(zipUrlStr);
    var client = GetHttpClient(smng);
    cancellationToken.ThrowIfCancellationRequested();
    var dlTask = client.GetAsync(zipUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    long contentLength;
    var baseDir = smng.GetItem<string>(SMNGKEY_BASE_DIR);
    var timestamp = DateTime.Now.ToString("yyyyMMddHHmmssfff");
    var downloadFile = string.Format("chrome-win32_{0}_{1}.zip", timestamp, revision);
    var downloadPath = Path.Combine(baseDir, downloadFile);
    using (var response = dlTask.Result)
    using (var content = response.Content) {
      contentLength = content.Headers.ContentLength ?? -1;
      OutputMessage(string.Format("Total {0} bytes", contentLength));
      var percentage = 0L;
      var prevPercentage = -1L;
      var current = 0L;

      smng.SetOrAddNewItem(SMNGKEY_DOWNLOAD_PATH, downloadPath);

      cancellationToken.ThrowIfCancellationRequested();
      using (var input = content.ReadAsStreamAsync().Result)
      using (var output = File.Open(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
        var buffer = new byte[8192];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) > 0) {
          cancellationToken.ThrowIfCancellationRequested();
          output.Write(buffer, 0, count);
          current += count;
          percentage = current * 100L / contentLength;
          if (prevPercentage < percentage) {
            prevPercentage = percentage;

            lock (consoleWriteLock) {
              Console.Write(string.Format("\b\b\b\b\b\b\b\b\b\b\b\b\b\b\b\bdownloading {0,3}%", percentage));
            }
          }
        }
        OutputMessage(string.Empty);
      }
    }

    var downloadFileInfo = new FileInfo(downloadPath);
    long fileLength = downloadFileInfo.Length;
    if (fileLength != contentLength) {
      throw new Exception(string.Format("Incomplete download, expected: {0}, actual: {1}", contentLength, fileLength));
    }
    if (!downloadFileInfo.Exists) {
      throw new Exception(string.Format("Download zip failed: {0}", downloadPath));
    }

    // ^chrome-win32_(?<timestamp>\d+)_[0-9a-f]+\.zip$
    var zipPattern = "^chrome-win32_(?<timestamp>\\d+)_[0-9a-f]+\\.zip$";
    int backupCycle;
    if (!smng.TryGetItem(SMNGKEY_BACKUP_CYCLE, out backupCycle)) {
      backupCycle = int.MaxValue;
    }
    var oldVersionZipQuery = Directory.GetFiles(baseDir)
      .Select(x => Path.GetFileName(x))
      .Select(x => Regex.Match(x, zipPattern))
      .Where(m => m.Success)
      .OrderByDescending(m => m.Groups["timestamp"].Value)
      .Skip(backupCycle)
      .Select(m => Path.Combine(baseDir, m.Value));

    foreach (string backupToDelete in oldVersionZipQuery) {
      if (File.Exists(backupToDelete)) {
        File.Delete(backupToDelete);
      }
      if (File.Exists(backupToDelete)) {
        cancellationToken.ThrowIfCancellationRequested();
        throw new Exception(string.Format("Error: delete old zip: {0}", backupToDelete));
      }
    }

    OutputDebug("DownloadZip end");
  }

  private static void Unzip(ISettingsManager smng) {
    OutputDebug("Unzip start");

    var cancellationToken = smng.GetItem<CancellationToken>(SMNGKEY_CANCELLATION_TOKEN).Value;
    var baseDir = smng.GetItem<string>(SMNGKEY_BASE_DIR);
    var unzipWorkDir = new DirectoryInfo(Path.Combine(baseDir, "unzipWork"));
    if (unzipWorkDir.Exists) {
      Directory.Delete(unzipWorkDir.FullName, true);
    }
    unzipWorkDir.Create();

    unzipWorkDir.Refresh();
    if (!unzipWorkDir.Exists) {
      throw new Exception(string.Format("Can't create directory: {0}", unzipWorkDir.FullName));
    }
    if (unzipWorkDir.EnumerateFileSystemInfos().Any()) {
      throw new Exception(string.Format("Can't delete directory contents: {0}", unzipWorkDir.FullName));
    }

    var unzip = smng.GetItem<string>(SMNGKEY_UNZIP);
    var downloadPath = smng.GetItem<string>(SMNGKEY_DOWNLOAD_PATH);
    var startInfo = new ProcessStartInfo(unzip) {
      Arguments = string.Format("\"{0}\"", downloadPath),
      CreateNoWindow = true,
      RedirectStandardOutput = true,
      RedirectStandardError = true,
      RedirectStandardInput = true,
      UseShellExecute = false,
      WindowStyle = ProcessWindowStyle.Hidden,
      WorkingDirectory = unzipWorkDir.FullName
    };

    using (var process = new Process()) {
      process.StartInfo = startInfo;
      DataReceivedEventHandler onDataReceived = delegate(object sender, DataReceivedEventArgs e) {
        var data = e.Data;
        OutputMessage(data);
      };
      process.OutputDataReceived += onDataReceived;
      process.ErrorDataReceived += onDataReceived;
      cancellationToken.ThrowIfCancellationRequested();
      process.Start();
      process.BeginOutputReadLine();
      process.BeginErrorReadLine();
      process.WaitForExit();

      var exitCode = process.ExitCode;
      if (exitCode != 0) {
        throw new Exception(string.Format("Unzip returned error: {0}", exitCode));
      }
    }

    var unzippedDir = Path.Combine(unzipWorkDir.FullName, "chrome-win32");
    if (!Directory.Exists(unzippedDir)) {
      throw new Exception(string.Format("Unzip failed, zip: {0}, destination: {1}", downloadPath, unzippedDir));
    }

    smng.SetOrAddNewItem(SMNGKEY_UNZIPPED_DIR, unzippedDir);

    OutputDebug("Unzip end");
  }

  private static void WaitChromiumExited(ISettingsManager smng) {
    OutputDebug("WaitChromiumExited start");

    var cancellationToken = smng.GetItem<CancellationToken>(SMNGKEY_CANCELLATION_TOKEN).Value;
    var exeName = smng.GetItem<string>(SMNGKEY_EXE_NAME);
    var sleepSec = smng.GetItem<int>(SMNGKEY_SLEEP_SEC);
    while (true) {
      var processes = Process.GetProcessesByName(exeName);
      try {
        if (processes.Length >= 1) {
          cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(sleepSec));
        }
        else {
          break;
        }
      }
      finally {
        foreach (var p in processes) {
          p.Dispose();
        }
      }
    }

    OutputDebug("WaitChromiumExited end");
  }

  private static void BackupExeDir(ISettingsManager smng) {
    OutputDebug("BackupExeDir start");

    var cancellationToken = smng.GetItem<CancellationToken>(SMNGKEY_CANCELLATION_TOKEN).Value;
    cancellationToken.ThrowIfCancellationRequested();

    var baseDir = smng.GetItem<string>(SMNGKEY_BASE_DIR);
    var currentBackupNum = Directory.GetDirectories(baseDir, "chrome-win32~*")
      .Select(x => int.Parse(Regex.Match(x, "chrome-win32~(\\d+)").Groups[1].Value))
      .OrderByDescending(x => x)
      .First();
    var backupNum = currentBackupNum + 1;
    var backupDir = Path.Combine(baseDir, string.Format("chrome-win32~{0}", backupNum));
    var appDir = Path.Combine(baseDir, "chrome-win32");
    Directory.Move(appDir, backupDir);

    if (Directory.Exists(appDir)) {
      throw new Exception(string.Format("Move directory failed, src: {0}", appDir));
    }
    if (!Directory.Exists(backupDir)) {
      throw new Exception(string.Format("Move directory failed, dst: {1}", backupDir));
    }

    int backupCycle;
    if (!smng.TryGetItem(SMNGKEY_BACKUP_CYCLE, out backupCycle)) {
      backupCycle = int.MaxValue;
    }
    var oldExeDirQuery = Directory.GetDirectories(baseDir)
      .Select(x => Path.GetFileName(x))
      .Where(x => Regex.IsMatch(x, "^chrome-win32~(?<num>\\d+)$")) // ^chrome-win32~(?<num>\d+)$
      .OrderByDescending(x => int.Parse(Regex.Match(x, "^chrome-win32~(?<num>\\d+)$").Groups["num"].Value))
      .Skip(backupCycle)
      .Select(x => Path.Combine(baseDir, x));

    foreach (string backupToDelete in oldExeDirQuery) {
      OutputDebug(string.Format("Deleting {0}", Path.GetFileName(backupToDelete)));
      Directory.Delete(backupToDelete, true);
    }

    OutputDebug("BackupExeDir end");
  }

  private static void BackupProfileDir(ISettingsManager smng) {
    OutputDebug("BackupProfileDir start");

    string ffc;
    string profileDir;
    string profileBackup;
    int profileBackupCycle;

    if (!smng.TryGetItem<string>("ffc", out ffc)) {
      return;
    }
    if (!smng.TryGetItem<string>("profileDir", out profileDir)) {
      return;
    }
    if (!smng.TryGetItem<string>("profileBackup", out profileBackup)) {
      return;
    }
    if (!smng.TryGetItem<int>("profileBackupCycle", out profileBackupCycle)) {
      return;
    }
    else if (profileBackupCycle < 1) {
      return;
    }

    if (File.Exists(ffc) && Directory.Exists(profileDir) && Directory.Exists(profileBackup)) {
      OutputMessage("Backup profile directory");
      var src = profileDir.TrimEnd('\\', '/');
      var dstNewDir = string.Format("ChromiumUserData_{0}", DateTime.Now.ToString("yyyyMMddHHmmssfff"));
      var dst = Path.Combine(profileBackup, dstNewDir);
      OutputMessage(string.Format("  {0} -> {1}", src, dst));
      var startInfo = new ProcessStartInfo(ffc) {
        Arguments = string.Format("\"{0}\" /to:\"{1}\" /ed /md /ft:15", src, dst),
        CreateNoWindow = false,
        UseShellExecute = false,
      };

      var cancellationToken = smng.GetItem<CancellationToken>(SMNGKEY_CANCELLATION_TOKEN).Value;
      cancellationToken.ThrowIfCancellationRequested();

      using (Process process = new Process()) {
        process.StartInfo = startInfo;
        process.Start();
        process.WaitForExit();
        int exitCode = process.ExitCode;
        if (exitCode != 0) {
          throw new Exception(string.Format("ffc has exited with code {0}", exitCode));
        }
      }

      if (!Directory.Exists(dst)) {
        throw new Exception(string.Format("Backup profile dir filed, dst: {0}", dst));
      }

      var oldProfileBackupQuery = Directory.GetDirectories(profileBackup)
        .Select(x => Path.GetFileName(x))
        .Where(x => Regex.IsMatch(x, "^ChromiumUserData_(\\d{17})$")) // ^ChromiumUserData_(\d{17})$
        .OrderByDescending(x => x)
        .Skip(profileBackupCycle)
        .Select(x => Path.Combine(profileBackup, x));
      bool isFirst = true;
      foreach (string oldProfileBackup in oldProfileBackupQuery) {
        if (isFirst) {
          isFirst = false;
          OutputMessage("Remove old profile backups");
        }
        OutputMessage(string.Format("  {0}", oldProfileBackup));
        Directory.Delete(oldProfileBackup, true);
      }
    }

    OutputDebug("BackupProfileDir end");
  }

  private static void RenameUnziped(ISettingsManager smng) {
    OutputDebug("RenameUnziped start");

    var unzippedDir = smng.GetItem<string>(SMNGKEY_UNZIPPED_DIR);
    var baseDir = smng.GetItem<string>(SMNGKEY_BASE_DIR);
    var appDir = Path.Combine(baseDir, "chrome-win32");
    Directory.Move(unzippedDir, appDir);

    if (Directory.Exists(unzippedDir)) {
      throw new Exception(string.Format("Rename unzipped dir failed, src: {0}", unzippedDir));
    }
    if (!Directory.Exists(appDir)) {
      throw new Exception(string.Format("Rename unzipped dir failed, dst: {0}", appDir));
    }

    OutputDebug("RenameUnziped end");
  }

  private static void OutputMessage(string message, TextWriter Writer) {
    var lines = message.Split('\r', '\n');
    DateTime now = DateTime.Now;
    lock (consoleWriteLock) {
      foreach (var m in lines) {
        Console.Error.WriteLine("{0}  {1}", now.ToString("yyyy-MM-ddTHH:mm:ss.fff"), m);
      }
    }
  }

  private static void OutputMessage(object message) {
    if (message == null) return;

    var m = (message as string) ?? message.ToString();
    OutputMessage(m, Console.Out);
  }

  private static void OutputErrorMessage(object message) {
    var m = (message as string) ?? message.ToString();
    OutputMessage(m, Console.Error);
  }

  private static void OutputError(Exception e) {
    var ae = e as AggregateException;
    if (ae == null) {
      if (errorMemo.TryAdd(e, null)) {
        OutputErrorMessage(e);
      }
    }
    else {
      foreach (var e2 in ae.InnerExceptions) {
        OutputError(e2);
      }
    }
  }

  private static void OutputDebug(string message) {
    Debug.WriteLine(message);
    OutputMessage(string.Format("DEBUG {0}", message));
  }
}
