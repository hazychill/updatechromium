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
    Uri zipUrl = smng.GetItem<Uri>("zipUrl");
    string exeName = smng.GetItem<string>("exeName");
    int sleepSec = smng.GetItem<int>("sleepSec");

    int rev = GetRevision(revUrl);
    string downloadFile = string.Format("chrome-win32_rev{0}.zip", rev);
    string downloadPath = Path.Combine(baseDir, downloadFile);
    if (File.Exists(downloadPath)) {
      OutputMessage("Latest.");
      return;
    }

    OutputMessage(string.Format("Downloading {0}", downloadPath));
    HttpWebRequest request = WebRequest.Create(zipUrl) as HttpWebRequest;
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

  private static int GetRevision(Uri revUrl) {
    HttpWebRequest request = WebRequest.Create(revUrl) as HttpWebRequest;
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

  private static void OutputMessage(object message) {
    DateTime now = DateTime.Now;
    Console.WriteLine("{0}  {1}", now.ToString("yyyy-MM-ddTHH:mm:ss.fff"), message);
  }

  private static void OutputError(object message) {
    DateTime now = DateTime.Now;
    Console.Error.WriteLine("{0}  {1}", now.ToString("yyyy-MM-ddTHH:mm:ss.fff"), message);
  }
}
