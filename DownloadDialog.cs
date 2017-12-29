﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows.Forms;
using Conduit.Sites;
using Conduit.Unpackers;

namespace Conduit
{
  public partial class DownloadDialog : Form
  {
    private Uri _openURL;

    public DownloadDialog(string openURL)
    {
      _openURL = new Uri(openURL);
      InitializeComponent();
    }

    private async Task<SiteProdInfo> GetDownloadURL()
    {
      var site = Registry.Sites.FirstOrDefault(s => s.Host == _openURL.Host);
      if (site == null)
      {
        return null;
      }

      return await site.RetrieveProdInfo(_openURL);
    }

    private void TransformURL(ref string url)
    {
      if (url.StartsWith("https://files.scene.org/view/"))
      {
        url = url.Replace("https://files.scene.org/view/", "https://files.scene.org/get:hu-http/");
      }
    }

    public string GetFormattedFileSize(int size)
    {
      double len = size;
      int order = 0;
      string[] sizes = { "B", "KB", "MB", "GB", "TB" };
      while (len >= 1024 && order < sizes.Length - 1)
      {
        order++;
        len = len / 1024;
      }
      return String.Format("{0:0.##} {1}", len, sizes[order]);
    }

    private async Task<string> DownloadFile(string url, string targetPath)
    {
      var filename = Path.GetFileName(new Uri(url).LocalPath);
      var localFileName = Path.Combine(targetPath, filename);

      if (File.Exists(localFileName))
      {
        downloadText.Text = "Already downloaded.";
        return localFileName;
      }

      downloadText.Text = $"Starting download from {url}...";

      string finalURL = url;
      WebResponse response = null;
      do
      {
        var request = WebRequest.Create(finalURL);
        if (request is HttpWebRequest)
        {
          (request as HttpWebRequest).AllowAutoRedirect = false;
        }
        response = await request.GetResponseAsync();
        if (request is HttpWebRequest)
        {
          finalURL = response.Headers["Location"] ?? finalURL;
          downloadProgress.Maximum = (int)response.ContentLength;
        }
      } while (response.Headers["Location"] != null);

      // Handle if redirect has changed the filename
      filename = Path.GetFileName(new Uri(finalURL).LocalPath);
      localFileName = Path.Combine(targetPath, filename);

      var stream = response.GetResponseStream();

      int bufferSize = 1024 * 1024;
      var bytes = new byte[bufferSize];
      var bytesRead = 0;
      var tmpFile = localFileName + ".$$$";
      if (File.Exists(tmpFile))
      {
        File.Delete(tmpFile);
      }
      using (FileStream fileStream = File.Open(tmpFile, FileMode.CreateNew))
      {
        var totalBytes = 0;
        do
        {
          bytesRead = await stream.ReadAsync(bytes, 0, bufferSize);
          fileStream.Write(bytes, 0, bytesRead);
          downloadProgress.Increment(bytesRead);
          if (downloadProgress.Maximum == 0)
          {
            downloadText.Text = $"Downloading [{filename}] ({GetFormattedFileSize(totalBytes)})...";
          }
          else
          {
            downloadText.Text = $"Downloading [{filename}] ({GetFormattedFileSize(totalBytes)} / {GetFormattedFileSize(downloadProgress.Maximum)})...";
          }
          totalBytes += bytesRead;
        } while (bytesRead > 0);
      }
      downloadText.Text = "Download finished!";
      File.Move(tmpFile, localFileName);

      return localFileName;
    }

    public static string Sanitize(string s)
    {
      return System.Text.RegularExpressions.Regex.Replace(s == null ? "[unknown]" : s, @"[^a-zA-Z0-9\-_\.]+", "-").ToLower();
    }

    private async Task<bool> UnpackFile(string archiveFile, string targetDir)
    {
      foreach (var unpacker in Registry.Unpackers)
      {
        if (unpacker.CanUnpack(archiveFile))
        {
          unpacker.ProgressChanged += (object sender, UnpackingProgressArgs e) =>
          {
            Dispatcher.CurrentDispatcher.Invoke(() =>
            {
              unpackProgress.Maximum = e.TotalFiles;
              unpackProgress.Value = e.CurrentFile;
              unpackText.Text = $"Unpacking {e.CurrentFilename}";
            });
          };
          Directory.CreateDirectory(targetDir);
          return await unpacker.Unpack(archiveFile, targetDir);
        }
      }
      return false;
    }

    private async Task WatchDemo()
    {
      var site = Registry.Sites.FirstOrDefault(s => s.Host == _openURL.Host);
      if (site == null)
      {
        return;
      }

      var prodInfo = await GetDownloadURL();
      if (prodInfo == null)
      {
        return;
      }

      this.Text = "Conduit - downloading demo: " + prodInfo.Name;

      var url = prodInfo.DownloadLink;
      TransformURL(ref url);

      var path = Settings.Options.DemoPath;
      path = path.Replace("[GROUP]", Sanitize(prodInfo.Group));
      path = path.Replace("[YEAR]", prodInfo.ReleaseDate.Year.ToString());
      Directory.CreateDirectory(path);

      var localFileName = await DownloadFile(url, path);
      if (localFileName == null)
      {
        return;
      }

      var extractPath = Path.Combine(path, Path.GetFileNameWithoutExtension(localFileName));
      if (!await UnpackFile(localFileName, extractPath))
      {
        // We couldn't unpack the file; maybe we can just run it?
        extractPath = localFileName;
      }
      foreach (var runner in Registry.Runners)
      {
        var files = runner.GetRunnableFiles(extractPath);
        if (files.Count == 0)
        {
          continue;
        }
        if (files.Count >= 1) // Temp hack, need to pop up dialog here.
        {
          runner.Run(files[0]);
          break;
        }
      }
    }

    private async void DownloadDialog_Load(object sender, EventArgs args)
    {
      try
      {
        await WatchDemo();
      }
      catch (Exception ex)
      {
        throw;
      }
      Close();
    }
  }
}
