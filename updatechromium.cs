using System;
using System.Net;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Threading;
using System.Reflection;
using System.Text;
using Hazychill.Setting;

public static class Program {
  private static object consoleWriteLock = new object();

  private const string MUTEX_NAME = "FE904DE5-11D9-420A-E54D-90FED9110A42";

  public static void Main() {
    Mutex mutex = null;
    try {
      bool createdNew;
      mutex = new Mutex(true, MUTEX_NAME, out createdNew);

      if (createdNew == false) {
        OutputMessage("updatechromium is already running.");
        return;
      }

      Exec();
    }
    catch (Exception e) {
      OutputError(e);
    }
    finally {
      while (Console.KeyAvailable) Console.ReadKey();
      OutputMessage("Press enter to exit");
      Console.ReadLine();

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

  private static void Exec() {
    SettingsManager smng = new SettingsManager();
    LoadSettings(smng);

    if (IsSuspended(smng)) {
      OutputMessage("updatechromium suspended.");
      return;
    }

    string baseDir = smng.GetItem<string>("baseDir");
    string unzip = smng.GetItem<string>("unzip");
    Uri revUrl = smng.GetItem<Uri>("revUrl");
    string zipUrlTemplate = smng.GetItem<string>("zipUrlTemplate");
    string exeName = smng.GetItem<string>("exeName");
    int sleepSec = smng.GetItem<int>("sleepSec");

    int rev = GetRevision(revUrl, smng);
    string downloadFile = string.Format("chrome-win32_rev{0}.zip", rev);
    string downloadPath = Path.Combine(baseDir, downloadFile);
    if (File.Exists(downloadPath)) {
      OutputMessage("Latest.");
      return;
    }

    OutputMessage(string.Format("Downloading {0}", downloadPath));
    string zipUrlStr = zipUrlTemplate.Replace("{revision}", rev.ToString());
    Uri zipUrl = new Uri(zipUrlStr);
    HttpWebRequest request = WebRequest.Create(zipUrl) as HttpWebRequest;
    GetProxySettings(request, smng);
    long contentLength;
    using (HttpWebResponse response = request.GetResponse() as HttpWebResponse) {
      contentLength = response.ContentLength;
      OutputMessage(string.Format("Total {0} bytes", contentLength));
      using (Stream input = response.GetResponseStream())
      using (Stream output = File.Open(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None)) {
        byte[] buffer = new byte[8192];
        int count;
        while ((count = input.Read(buffer, 0, buffer.Length)) > 0) {
          output.Write(buffer, 0, count);
        }
      }
    }

    FileInfo downloadFileInfo = new FileInfo(downloadPath);
    long fileLength = downloadFileInfo.Length;
    if (fileLength != contentLength) {
      OutputError("Error occurred while downloading file");
      OutputError(string.Format("  ContentLength: {0}, Downloaded: {1}",
                                contentLength, fileLength));
      return;
    }

    if (IsExeRunning(exeName)) {
      OutputMessage("Waiting chromium for exit");
      WaitExeForExit(exeName, sleepSec);
    }

    int backupNum = Unzip(unzip, downloadPath, baseDir);

    OutputMessage("Remove old versions");
    int backupCycle;
    if (!smng.TryGetItem("backupCycle", out backupCycle)) {
      backupCycle = int.MaxValue;
    }
    var oldVersionDirectoryQuery = Directory.GetDirectories(baseDir)
      .Select(x => Path.GetFileName(x))
      .Where(x => Regex.IsMatch(x, "^chrome-win32~(?<num>\\d+)$")) // ^chrome-win32~(?<num>\d+)$
      .OrderByDescending(x => int.Parse(Regex.Match(x, "^chrome-win32~(?<num>\\d+)$").Groups["num"].Value))
      .Skip(backupCycle)
      .Select(x => Path.Combine(baseDir, x));

    foreach (string backupToDelete in oldVersionDirectoryQuery) {
      Directory.Delete(backupToDelete, true);
    }

    var oldVersionZipQuery = Directory.GetFiles(baseDir)
      .Select(x => Path.GetFileName(x))
      .Where(x => Regex.IsMatch(x, "^chrome-win32_rev(?<rev>\\d+)\\.zip$")) // ^chrome-win32_rev(?<rev>\d+)\.zip$
      .OrderByDescending(x => int.Parse(Regex.Match(x, "^chrome-win32_rev(?<rev>\\d+)\\.zip$").Groups["rev"].Value))
      .Skip(backupCycle)
      .Select(x => Path.Combine(baseDir, x));

    foreach (string backupToDelete in oldVersionZipQuery) {
      File.Delete(backupToDelete);
    }

    BackupProfile(smng);

    OutputMessage("All done");
  }

  private static void LoadSettings(SettingsManager smng) {
    string execDir = GetExecDir();
    string settingsFilePath = Path.Combine(execDir, "settings.txt");
    smng.Load(settingsFilePath);
  }

  private static string GetExecDir() {
    Assembly myAssembly = Assembly.GetEntryAssembly();
    string path = myAssembly.Location;
    return Path.GetDirectoryName(path);
  }

  private static bool IsSuspended(ISettingsManager smng) {
    string suspendFileName;
    if (smng.TryGetItem("suspendFileName", out suspendFileName)) {
      string execDir = GetExecDir();
      string suspendFilePath = Path.Combine(execDir, suspendFileName);
      return File.Exists(suspendFilePath);
    }
    else {
      return false;
    }
  }

  private static int GetRevision(Uri revUrl, ISettingsManager smng) {
    HttpWebRequest request = WebRequest.Create(revUrl) as HttpWebRequest;
    GetProxySettings(request, smng);
    using (HttpWebResponse response = request.GetResponse() as HttpWebResponse)
    using (Stream responseStream = response.GetResponseStream())
    using (TextReader reader = new StreamReader(responseStream, new UTF8Encoding())) {
      string revStr = reader.ReadLine();
      int rev = int.Parse(revStr);
      return rev;
    }
  }

  private static bool IsExeRunning(string exeName) {
    Process[] processes = Process.GetProcessesByName(exeName);
    try {
      return (processes.Length >= 1);
    }
    finally {
      foreach (Process process in processes) {
        process.Dispose();
      }
    }
  }

  private static void WaitExeForExit(string exeName, int sleepSec) {
    do {
      Thread.Sleep(sleepSec * 1000);
    } while (IsExeRunning(exeName));
  }

  private static int Unzip(string unzip, string downloadPath, string baseDir) {
    int currentBackupNum = Directory.GetDirectories(baseDir, "chrome-win32~*")
      .Select(x => int.Parse(Regex.Match(x, "chrome-win32~(\\d+)").Groups[1].Value))
      .OrderByDescending(x => x)
      .First();
    int backupNum = currentBackupNum + 1;
    string backupDir = Path.Combine(baseDir, string.Format("chrome-win32~{0}", backupNum));
    string appDir = Path.Combine(baseDir, "chrome-win32");
    Directory.Move(appDir, backupDir);

    ProcessStartInfo startInfo = new ProcessStartInfo(unzip);
    startInfo.Arguments = string.Format("\"{0}\"", downloadPath);
    startInfo.CreateNoWindow = true;
    startInfo.RedirectStandardOutput = true;
    startInfo.RedirectStandardError = true;
    startInfo.UseShellExecute = false;
    startInfo.WindowStyle = ProcessWindowStyle.Hidden;
    startInfo.WorkingDirectory = baseDir;

    using (Process process = new Process()) {
      process.StartInfo = startInfo;
      DataReceivedEventHandler onDataReceived = delegate(object sender, DataReceivedEventArgs e) {
        string data = e.Data;
        lock (consoleWriteLock) {
          OutputMessage(data);
        }
      };
      process.OutputDataReceived += onDataReceived;
      process.ErrorDataReceived += onDataReceived;
      process.Start();
      process.BeginOutputReadLine();
      process.BeginErrorReadLine();
      process.WaitForExit();
    }

    return backupNum;
  }

  private static void GetProxySettings(HttpWebRequest request, ISettingsManager smng) {
    string proxyHost;
    int proxyPort;
    if (smng.TryGetItem("proxyHost", out proxyHost) &&
        smng.TryGetItem("proxyPort", out proxyPort)) {
      request.Proxy = new WebProxy(proxyHost, proxyPort);
    }
  }

  private static void OutputMessage(object message) {
    DateTime now = DateTime.Now;
    Console.WriteLine("{0}  {1}", now.ToString("yyyy-MM-ddTHH:mm:ss.fff"), message);
  }

  private static void OutputError(object message) {
    DateTime now = DateTime.Now;
    Console.Error.WriteLine("{0}  {1}", now.ToString("yyyy-MM-ddTHH:mm:ss.fff"), message);
  }

  private static void BackupProfile(ISettingsManager smng) {
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
      string src = profileDir.TrimEnd('\\', '/');
      string dstNewDir = string.Format("ChromiumUserData_{0}", DateTime.Now.ToString("yyyyMMddHHmmssfff"));
      string dst = Path.Combine(profileBackup, dstNewDir);
      OutputMessage(string.Format("  {0} -> {1}", src, dst));
      ProcessStartInfo startInfo = new ProcessStartInfo(ffc);

      startInfo.Arguments = string.Format("\"{0}\" /to:\"{1}\" /ed /md /ft:15", src, dst);
      startInfo.CreateNoWindow = false;
      startInfo.UseShellExecute = false;
      using (Process process = new Process()) {
        process.StartInfo = startInfo;
        process.Start();
        process.WaitForExit();
        int exitCode = process.ExitCode;
        if (exitCode == 0) {
          DeleteOldProfileBackup(profileBackup, profileBackupCycle);
        }
        else {
          OutputError(string.Format("ffc has exited with code {0}", exitCode));
        }
      }
    }
  }

  private static void DeleteOldProfileBackup(string profileBackup, int profileBackupCycle) {
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
}
