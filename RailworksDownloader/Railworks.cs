﻿using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Xml;
using static RailworksDownloader.Utils;

namespace RailworksDownloader
{
    internal class Railworks
    {
        private string rwPath;
        public string RWPath
        {
            get => rwPath;
            set
            {
                rwPath = value;

                if (rwPath != null)
                    AssetsPath = NormalizePath(Path.Combine(RWPath, "Assets"));
            }
        }

        public string AssetsPath { get; set; }

        public List<RouteInfo> Routes { get; set; }

        private readonly HashSet<string> APDependencies = new HashSet<string>();
        private int Total = 0;
        private float Elapsed = 0f;
        private int Completed = 0;
        private readonly object PercentLock = new object();
        private readonly object CompleteLock = new object();
        private readonly object SavingLock = new object();
        private readonly object APDepsLock = new object();
        private int Saving = 0;

        public EventWaitHandle getAllInstalledDepsEvent = new EventWaitHandle(false, EventResetMode.ManualReset);

        public delegate void ProgressUpdatedEventHandler(int percent);
        public event ProgressUpdatedEventHandler ProgressUpdated;

        public delegate void RouteSavingEventHandler(bool saved);
        public event RouteSavingEventHandler RouteSaving;

        public delegate void CompleteEventHandler();
        public event CompleteEventHandler CrawlingComplete;

        public HashSet<string> AllRequiredDeps { get; set; } = new HashSet<string>();

        public HashSet<string> AllInstalledDeps { get; set; } = new HashSet<string>();

        public IEnumerable<string> AllMissingDeps { get; set; } = new string[0];

        public Railworks(string path = null)
        {
            RWPath = string.IsNullOrWhiteSpace(path) ? GetRWPath() : path;

            if (RWPath != null)
                AssetsPath = Utils.NormalizePath(Path.Combine(RWPath, "Assets"));

            Routes = new List<RouteInfo>();
        }

        public void InitRoutes()
        {
            Routes = GetRoutes().ToList();
        }

        public static string GetRWPath()
        {
            string path = (string)Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\RailSimulator.com\RailWorks", false)?.GetValue("install_path");

            if (path != null)
                return path;
            else
                return (string)Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 24010", false)?.GetValue("InstallLocation");
        }

        private string ParseDisplayNameNode(XmlNode displayNameNode)
        {
            foreach (XmlNode n in displayNameNode.FirstChild)
            {
                if (!string.IsNullOrEmpty(n.InnerText))
                    return n.InnerText;
            }

            return null;
        }

        private string ParseRouteProperties(Stream istream, string file)
        {
            if (istream.Length > 4)
            {
                Stream stream = new MemoryStream();
                istream.CopyTo(stream);
                istream.Close();
                stream.Seek(0, SeekOrigin.Begin);

                if (Utils.CheckIsSerz(stream))
                {
                    SerzReader sr = new SerzReader(stream, file, SerzReader.MODES.routeName);
                    return sr.RouteName;
                }
                else
                {
                    try
                    {
                        XmlDocument doc = new XmlDocument();
                        doc.Load(XmlReader.Create(RemoveInvalidXmlChars(stream), new XmlReaderSettings() { CheckCharacters = false }));

                        return ParseDisplayNameNode(doc.DocumentElement.SelectSingleNode("DisplayName"));
                    }
                    catch (Exception)
                    {
                        MessageBox.Show(string.Format(Localization.Strings.ParseRoutePropFail, file), Localization.Strings.ParseRoutePropFailTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
            }
            return default;
        }

        private string ParseRouteProperties(string fpath)
        {
            using (Stream fs = File.OpenRead(fpath))
            {
                return ParseRouteProperties(fs, fpath);
            }
        }

        internal static void DeleteDirectory(string directory)
        {
            if (Directory.Exists(directory))
            {
                string[] files = Directory.GetFiles(directory);
                string[] dirs = Directory.GetDirectories(directory);

                foreach (string file in files)
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                    File.Delete(file);
                }

                foreach (string dir in dirs)
                {
                    DeleteDirectory(dir);
                }

                Directory.Delete(directory, true);
            }
        }

        /// <summary>
        /// Get list of routes
        /// </summary>
        /// <param name="path">Routes path</param>
        /// <returns></returns>
        public IEnumerable<RouteInfo> GetRoutes()
        {
            string path = Path.Combine(RWPath, "Content", "Routes");
            List<RouteInfo> list = new List<RouteInfo>();

            foreach (string dir in Directory.GetDirectories(path))
            {
                string rp_path = Utils.FindFile(dir, "RouteProperties.*");

                if (File.Exists(rp_path))
                {
                    list.Add(new RouteInfo(ParseRouteProperties(rp_path).Trim(), Path.GetFileName(dir), dir + Path.DirectorySeparatorChar));
                }
                else
                {
                    foreach (string file in Directory.GetFiles(dir, "*.ap"))
                    {
                        try
                        {
                            using (ZipArchive archive = System.IO.Compression.ZipFile.OpenRead(file))
                            {
                                foreach (ZipArchiveEntry entry in archive.Entries.Where(e => e.FullName.Contains("RouteProperties")))
                                {
                                    list.Add(new RouteInfo(ParseRouteProperties(entry.Open(), Path.Combine(file, entry.FullName)).Trim(), Path.GetFileName(dir), dir + Path.DirectorySeparatorChar));
                                    break;
                                }
                            }
                        }
                        catch
                        {
                            Trace.Assert(false, string.Format(Localization.Strings.ReadingZipFail, file));
                        }
                    }
                }
            }

            return list;
        }

        internal void InitCrawlers()
        {
            Total = 0;
            object total_lock = new object();
            int maxThreads = Math.Min(Environment.ProcessorCount, Routes.Count);
            Parallel.For(0, maxThreads, workerId =>
            {
                int max = Routes.Count * (workerId + 1) / maxThreads;
                for (int i = Routes.Count * workerId / maxThreads; i < max; i++)
                {
                    RouteInfo ri = Routes[i];
                    ri.Progress = 0;
                    ri.Crawler = new RouteCrawler(ri.Path, ri.Dependencies, ri.ScenarioDeps);
                    ri.Crawler.DeltaProgress += OnProgress;
                    ri.Crawler.ProgressUpdated += ri.ProgressUpdated;
                    ri.Crawler.Complete += Complete;
                    ri.Crawler.RouteSaving += Crawler_RouteSaving;
                    lock (total_lock)
                        Total += 100;
                }
            });
        }

        private void Crawler_RouteSaving(bool saved)
        {
            lock (SavingLock)
            {
                if (saved)
                    Saving--;
                else
                    Saving++;
            }

            RouteSaving?.Invoke(Saving == 0);
        }

        internal void RunAllCrawlers()
        {
            InitCrawlers();

            APDependencies.Clear();

            int maxThreads = Math.Min(Environment.ProcessorCount, Routes.Count);
            Parallel.For(0, maxThreads, workerId =>
            {
                int max = Routes.Count * (workerId + 1) / maxThreads;
                for (int i = Routes.Count * workerId / maxThreads; i < max; i++)
                {
                    Routes[i].Crawler.Start();
                }
            });
        }

        private void Complete()
        {
            lock (CompleteLock)
            {
                Completed++;

                if (Completed == Routes.Count)
                    CrawlingComplete?.Invoke();
            }
        }

        private void OnProgress(float percent)
        {
            lock (PercentLock)
            {
                Elapsed += percent;

                ProgressUpdated?.Invoke((int)(Elapsed * 100 / Total));
            }
        }

        private bool CheckForFileInAP(string directory, string fileToFind)
        {
            if (NormalizePath(directory) == NormalizePath(AssetsPath))
            {
                return false;
            }
            else
            {
                if (Directory.Exists(directory))
                {
                    foreach (string file in Directory.GetFiles(directory, "*.ap"))
                    {
                        try
                        {
                            ZipArchive zipFile = System.IO.Compression.ZipFile.OpenRead(file);

                            lock (APDepsLock)
                                APDependencies.UnionWith(from x in zipFile.Entries where (x.FullName.Contains(".xml") || x.FullName.Contains(".bin")) select NormalizePath(GetRelativePath(AssetsPath, Path.Combine(directory, x.FullName))));
                        }
                        catch { }
                    }
                    if (APDependencies.Contains(fileToFind) || APDependencies.Contains(NormalizePath(fileToFind, "xml")))
                    {
                        return true;
                    }
                }
                return CheckForFileInAP(Directory.GetParent(directory).FullName, fileToFind);
            }
        }

        public void GetInstalledDeps()
        {
            AllInstalledDeps = new HashSet<string>();
            string[] files = Directory.GetFiles(AssetsPath, "*.*", SearchOption.AllDirectories);
            foreach (string file in files)
            {
                string ext = Path.GetExtension(file).ToLower();
                if (ext == ".bin" || ext == ".xml")
                {
                    AllInstalledDeps.Add(NormalizePath(GetRelativePath(AssetsPath, file)));
                }
                else if (ext == ".ap")
                {
                    try
                    {
                        using (ICSharpCode.SharpZipLib.Zip.ZipFile zip = new ICSharpCode.SharpZipLib.Zip.ZipFile(file))
                        {
                            foreach (ZipEntry entry in zip)
                            {
                                string iExt = Path.GetExtension(entry.Name).ToLower();
                                if (iExt == ".xml" || iExt == ".bin")
                                {
                                    AllInstalledDeps.Add(NormalizePath(GetRelativePath(AssetsPath, Path.Combine(Path.GetDirectoryName(file), entry.Name))));
                                }
                            }
                        }
                    }
                    catch
                    {
                        Debug.Assert(false, string.Format(Localization.Strings.ReadingZipFail, file));
                    }
                }
            }
            getAllInstalledDepsEvent.Set();
        }

        public async Task<HashSet<string>> GetInstalledDeps(HashSet<string> globalDeps)
        {
            HashSet<string> existingDeps = new HashSet<string>();

            await Task.Run(() =>
            {
                int maxThreads = Math.Min(Environment.ProcessorCount, globalDeps.Count);
                Parallel.For(0, maxThreads, workerId =>
                {
                    int max = globalDeps.Count * (workerId + 1) / maxThreads;
                    for (int i = globalDeps.Count * workerId / maxThreads; i < max; i++)
                    {
                        string dependency = globalDeps.ElementAt(i);
                        if (!string.IsNullOrWhiteSpace(dependency))
                        {
                            string path = NormalizePath(Path.Combine(AssetsPath, dependency), "xml");
                            string path_bin = NormalizePath(path, "bin");
                            string relative_path = NormalizePath(GetRelativePath(AssetsPath, path));
                            string relative_path_bin = NormalizePath(relative_path, ".bin");

                            bool exists = APDependencies.Contains(relative_path_bin) || APDependencies.Contains(relative_path) || File.Exists(path_bin) || File.Exists(path) || CheckForFileInAP(Directory.GetParent(path).FullName, relative_path);

                            if (exists)
                                lock (existingDeps)
                                    existingDeps.Add(dependency);
                        }
                    }
                });
            });

            return existingDeps;
        }
    }
}
