using System;
using System.Net;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Diagnostics;

public static class Program {
  public static void Main() {
    string baseDir = @"C:\tools\Chromium";
    string winrar = @"C:\Program Files\WinRAR\WinRAR.exe";
    UriBuilder uriBuilder = new UriBuilder("http://build.chromium.org/");

    using (WebClient client = new WebClient()) {
      uriBuilder.Path = "/f/chromium/continuous/win/";
      string latestDateStr = Regex.Matches(client.DownloadString(uriBuilder.Uri), "\\d{4}-\\d{2}-\\d{2}")
        .Cast<Match>()
        .Select(m => m.Value)
        .OrderByDescending(date => date)
        .First();

      uriBuilder.Path = string.Format("/f/chromium/continuous/win/{0}/", latestDateStr);
      string latestRevStr = Regex.Matches(client.DownloadString(uriBuilder.Uri), "href=\"(?<rev>\\d+)/\"")
        .Cast<Match>()
        .Select(m => m.Groups["rev"].Value)
        .OrderByDescending(revStr => int.Parse(revStr))
        .First();

      string downloadFile = string.Format("chrome-win32_rev{0}.zip", latestRevStr);
      string downloadPath = Path.Combine(baseDir, downloadFile);
      if (File.Exists(downloadPath)) {
        Console.WriteLine("Latest");
        Console.ReadLine();
        return;
      }

      uriBuilder.Path = string.Format("/f/chromium/continuous/win/{0}/{1}/chrome-win32.zip", latestDateStr, latestRevStr);
      Console.WriteLine("Downloading");
      client.DownloadFile(uriBuilder.Uri, downloadPath);

      Console.WriteLine("Exit chromium");
      Console.ReadLine();

      int currentBackupNum = Directory.GetDirectories(baseDir, "chrome-win32~*")
        .Select(x => int.Parse(Regex.Match(x, "chrome-win32~(\\d+)").Groups[1].Value))
        .OrderByDescending(x => x)
        .First();
      int backupNum = currentBackupNum + 1;
      string backupDir = Path.Combine(baseDir, string.Format("chrome-win32~{0}", backupNum));
      string appDir = Path.Combine(baseDir, "chrome-win32");
      Directory.Move(appDir, backupDir);

      string commandArg = string.Format("X \"{0}\"", downloadPath);
      ProcessStartInfo startInfo = new ProcessStartInfo(winrar);
      startInfo.Arguments = commandArg;
      startInfo.WorkingDirectory = baseDir;
      using (Process process = new Process()) {
        process.StartInfo = startInfo;
        Console.WriteLine("Extracting");
        process.Start();
        process.WaitForExit();
      }
    }

    Console.WriteLine("Done");
    Console.ReadLine();
  }
}
