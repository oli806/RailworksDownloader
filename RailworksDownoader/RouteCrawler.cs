﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Xml;

namespace RailworksDownoader
{
    class RouteCrawler
    {
        private string RoutePath;
        private string RailworksPath;
        private long AllFilesSize = 0;
        private long Progress = 0;
        private object DependenciesLock = new object();
        private object ProgressLock = new object();
        private int PercentProgress = 0;
        private LoadedRoute SavedRoute;
        private SqLiteAdapter Adapter;

        internal bool IsAP { get => !File.Exists(Path.Combine(RoutePath, "RouteProperties.xml")); }

        public delegate void ProgressUpdatedEventHandler(int percent);
        public event ProgressUpdatedEventHandler ProgressUpdated;

        public delegate void CrawlingCompleteEventHandler();
        public event CrawlingCompleteEventHandler Complete;

        public HashSet<string> Dependencies { get; private set; }

        public RouteCrawler(string path, string railworksPath)
        {
            RoutePath = path;
            RailworksPath = railworksPath;
            Dependencies = new HashSet<string>();
            Adapter = new SqLiteAdapter(Path.Combine(RoutePath, "cache.dls"));
            SavedRoute = Adapter.LoadSavedRoute(IsAP);
        }

        public async Task Start()
        {
            if (Directory.Exists(RoutePath))
            {
                AllFilesSize = CountAllFiles();

                await GetDependencies();

                if (PercentProgress != 100)
                    PercentProgress = 100;

                Complete?.Invoke();
            }
        }

        private void ReportProgress(string file)
        {
            lock (ProgressLock)
            {
                Progress += GetFileSize(file);
            }

            Debug.Assert(Progress <= AllFilesSize, "Fatal, Progress is bigger than size of all files! " + Progress + ":" + AllFilesSize+"\nRoute: "+RoutePath);

            Progress = Math.Min(Progress, AllFilesSize);

            if (AllFilesSize > 0)
                PercentProgress = (int)(Progress * 100 / AllFilesSize);
            else
                PercentProgress = 100;

            ProgressUpdated?.Invoke(PercentProgress);
        }

        private string ParseAbsoluteBlueprintIDNode(XmlNode blueprintIDNode)
        {
            var absoluteBlueprintID = blueprintIDNode.FirstChild;
            var blueprintSetID = absoluteBlueprintID.FirstChild.FirstChild;
            return Path.Combine(blueprintSetID.FirstChild.InnerText, blueprintSetID.LastChild.InnerText, absoluteBlueprintID.LastChild.InnerText);
        }

        private IEnumerable<string> ParseSkiesNode(XmlNode skiesNode)
        {
            foreach (XmlNode n in skiesNode.FirstChild)
            {
                yield return ParseAbsoluteBlueprintIDNode(n);
            }
        }

        private async Task<IEnumerable<string>> GetRoutePropertiesDependencies(string propertiesPath)
        {
            var dependencies = new List<string>();

            XmlDocument doc = new XmlDocument();
            doc.Load(propertiesPath);

            var root = doc.DocumentElement;

            await Task.Run(() =>
            {
                dependencies.Add(ParseAbsoluteBlueprintIDNode(root.SelectSingleNode("BlueprintID")));
                dependencies.AddRange(ParseSkiesNode(root.SelectSingleNode("Skies")));
                dependencies.Add(ParseAbsoluteBlueprintIDNode(root.SelectSingleNode("WeatherBlueprint")));
                dependencies.Add(ParseAbsoluteBlueprintIDNode(root.SelectSingleNode("TerrainBlueprint")));
                dependencies.Add(ParseAbsoluteBlueprintIDNode(root.SelectSingleNode("MapBlueprint")));
            });

            return dependencies;
        }

        private IEnumerable<string> ParseBlueprint(string blueprintPath)
        {
            XmlDocument doc = new XmlDocument();
            doc.Load(blueprintPath);

            foreach (XmlNode node in doc.SelectNodes("//Provider"))
            {
                var blueprintSetID = node.ParentNode;
                var absoluteBlueprintID = blueprintSetID.ParentNode.ParentNode;
                yield return Path.Combine(blueprintSetID.FirstChild.InnerText, blueprintSetID.LastChild.InnerText, absoluteBlueprintID.LastChild.InnerText);
            }
        }

        private async Task _GetScenariosDependencies(string scenarioDir)
        {
            foreach (string file in Directory.GetFiles(scenarioDir, "*.*bin", SearchOption.AllDirectories))
            {
                string xml = Path.ChangeExtension(file, ".xml");

                await Task.Run(() => RunSERZ(file));
                try
                {
                    lock (DependenciesLock)
                    {
                        Dependencies.UnionWith(ParseBlueprint(xml));
                        ReportProgress(file);
                    }
                }
                catch { }
                File.Delete(xml);
            }

            string scenarioProperties = Path.Combine(scenarioDir, "ScenarioProperties.xml");
            if (File.Exists(scenarioProperties))
            {
                lock (DependenciesLock)
                {
                    Dependencies.UnionWith(ParseBlueprint(scenarioProperties));
                    ReportProgress(scenarioProperties);
                }
            }
        }

        private async Task GetScenariosDependencies(string scenariosDir)
        {
            foreach (string scenarioDir in Directory.GetDirectories(scenariosDir))
            {
                int APs_count = 0;
                foreach (string file in Directory.GetFiles(scenarioDir, "*.ap"))
                {
                    try
                    {
                        ZipFile.ExtractToDirectory(Path.Combine(scenarioDir, file), Path.Combine(scenarioDir, "temp"));
                    }
                    catch { }
                    finally
                    {
                        APs_count++;
                    }
                }

                if (APs_count > 0)
                {
                    await _GetScenariosDependencies(Path.Combine(scenarioDir, "temp"));

                    await Task.Run(() => { DeleteDirectory(Path.Combine(scenarioDir, "temp")); });
                } else
                {
                    await _GetScenariosDependencies(scenarioDir);
                }
            }
        }

        private async Task GetNetworkDependencies(string path)
        {
            foreach (string dir in Directory.GetDirectories(path))
            {
                foreach (string file in Directory.GetFiles(dir, "*.*bin"))
                {
                    string xml = Path.ChangeExtension(file, ".xml");

                    await Task.Run(() => RunSERZ(file));
                    try
                    {
                        lock (DependenciesLock)
                        {
                            Dependencies.UnionWith(ParseBlueprint(xml));
                            ReportProgress(file);
                        }
                    }
                    catch { }
                    File.Delete(xml);
                }
            }
        }

        private async Task GetSceneryDependencies(string path)
        {
            foreach (string file in Directory.GetFiles(path, "*.*bin"))
            {
                string xml = Path.ChangeExtension(file, ".xml");

                await Task.Run(() => RunSERZ(file));
                try
                {
                    lock (DependenciesLock)
                    {
                        Dependencies.UnionWith(ParseBlueprint(xml));
                        ReportProgress(file);
                    }
                }
                catch { }
                File.Delete(xml);
            }
        }

        private void RunSERZ(string file)
        {
            string xml = Path.ChangeExtension(file, ".xml");

            ProcessStartInfo si = new ProcessStartInfo();
            si.FileName = Path.Combine(RailworksPath, "serz.exe");
            si.Arguments = $"\"{file}\" /xml:\"{xml}\"";
            si.RedirectStandardOutput = true;
            si.RedirectStandardError = true;
            si.UseShellExecute = false;
            si.CreateNoWindow = true;

            Process serz = new Process();
            serz.StartInfo = si;

            serz.Start();
            serz.WaitForExit();
        }

        private async Task _GetDependencies()
        {
            bool apChanged = false;

            if (IsAP)
            {
                apChanged = GetDirectoryMD5(RoutePath, true) != SavedRoute.APChecksum;
            }

            bool loftsChanged = (GetDirectoryMD5(Path.Combine(RoutePath, "Networks", "Loft Tiles")) != SavedRoute.LoftChecksum) || apChanged;
            bool roadsChanged = (GetDirectoryMD5(Path.Combine(RoutePath, "Networks", "Road Tiles")) != SavedRoute.RoadChecksum) || apChanged;
            bool tracksChanged = (GetDirectoryMD5(Path.Combine(RoutePath, "Networks", "Track Tiles")) != SavedRoute.TrackChecksum) || apChanged;
            bool sceneryChanged = (GetDirectoryMD5(Path.Combine(RoutePath, "Scenery")) != SavedRoute.SceneryChecksum) || apChanged;
            bool rpChanged = (GetFileMD5(Path.Combine(RoutePath, "RouteProperties.xml")) != SavedRoute.RoutePropertiesChecksum) || apChanged;
            bool scenariosChanged = (GetDirectoryMD5(Path.Combine(RoutePath, "Scenarios")) != SavedRoute.ScenariosChecksum) || apChanged;

            var md5 = ComputeChecksums();

            if (SavedRoute.Dependencies != null)
                Dependencies.UnionWith(SavedRoute.Dependencies);

            if (loftsChanged || roadsChanged || tracksChanged || sceneryChanged || rpChanged || scenariosChanged)
            {
                Task n1 = null;
                Task s1 = null;
                Task t1 = null;
                Task n2 = null;
                Task s2 = null;
                Task t2 = null;
                Task<IEnumerable<string>> prop1 = null;
                Task<IEnumerable<string>> prop2 = null;

                if (rpChanged)
                    prop1 = GetRoutePropertiesDependencies(Path.Combine(RoutePath, "RouteProperties.xml"));
                if (IsAP)
                    prop2 = GetRoutePropertiesDependencies(Path.Combine(RoutePath, "temp", "RouteProperties.xml"));

                foreach (string dir in Directory.GetDirectories(RoutePath))
                {
                    switch (Path.GetFileName(dir))
                    {
                        case "Networks":
                            if (loftsChanged || roadsChanged || tracksChanged)
                                n1 = GetNetworkDependencies(dir);
                            break;
                        case "Scenarios":
                            if (scenariosChanged)
                                s1 = GetScenariosDependencies(dir);
                            break;
                        case "Scenery":
                            if (sceneryChanged)
                                t1 = GetSceneryDependencies(dir);
                            break;
                    }
                }

                if (IsAP)
                {

                    foreach (string dir in Directory.GetDirectories(Path.Combine(RoutePath, "temp")))
                    {
                        switch (Path.GetFileName(dir))
                        {
                            case "Networks":
                                if (loftsChanged || roadsChanged || tracksChanged)
                                    n2 = GetNetworkDependencies(dir);
                                break;
                            case "Scenarios":
                                if (scenariosChanged)
                                    s2 = GetScenariosDependencies(dir);
                                break;
                            case "Scenery":
                                if (sceneryChanged)
                                    t2 = GetSceneryDependencies(dir);
                                break;
                        }
                    }
                }

                if (n1 != null)
                    await n1;
                if (s1 != null)
                    await s1;
                if (t1 != null)
                    await t1;

                if (n2 != null)
                    await n2;
                if (s2 != null)
                    await s2;
                if (t2 != null)
                    await t2;

                IEnumerable<string> routeProperties1 = new List<string>();
                if (prop1 != null)
                    routeProperties1 = await prop1;

                lock (DependenciesLock)
                {
                    Dependencies.UnionWith(routeProperties1);
                    ReportProgress(Path.Combine(RoutePath, "RouteProperties.xml"));
                }

                if (IsAP)
                {
                    IEnumerable<string> routeProperties2 = new List<string>();
                    if (prop2 != null)
                        routeProperties2 = await prop2;

                    lock (DependenciesLock)
                    {
                        Dependencies.UnionWith(routeProperties2);
                        ReportProgress(Path.Combine(RoutePath, "temp", "RouteProperties.xml"));
                    }
                }

                Dependencies.RemoveWhere(x => string.IsNullOrWhiteSpace(x));

                await md5;

                SavedRoute.Dependencies = Dependencies.ToList();

                Thread tt = new Thread(() => Adapter.SaveRoute(SavedRoute));
                tt.Start();
            }
        }

        private async Task ComputeChecksums()
        {
            await Task.Run(() =>
            {
                if (IsAP)
                {
                    SavedRoute.APChecksum = GetDirectoryMD5(RoutePath, true);
                }
                else
                {
                    SavedRoute.RoutePropertiesChecksum = GetFileMD5(Path.Combine(RoutePath, "RouteProperties.xml"));
                    SavedRoute.LoftChecksum = GetDirectoryMD5(Path.Combine(RoutePath, "Networks", "Loft Tiles"));
                    SavedRoute.RoadChecksum = GetDirectoryMD5(Path.Combine(RoutePath, "Networks", "Road Tiles"));
                    SavedRoute.TrackChecksum = GetDirectoryMD5(Path.Combine(RoutePath, "Networks", "Track Tiles"));
                    SavedRoute.SceneryChecksum = GetDirectoryMD5(Path.Combine(RoutePath, "Scenery"));
                    SavedRoute.ScenariosChecksum = GetDirectoryMD5(Path.Combine(RoutePath, "Scenarios"));
                }
            });
        }

        private async Task GetDependencies()
        {
            if (File.Exists(Path.Combine(RoutePath, "RouteProperties.xml")))
            {
                await _GetDependencies();
            }
            else if (IsAP && GetDirectoryMD5(RoutePath, true) != SavedRoute.APChecksum)
            {
                int count = 0;
                foreach (string file in Directory.GetFiles(RoutePath, "*.ap"))
                {
                    /*try
                    {
                        ZipFile.ExtractToDirectory(Path.Combine(RoutePath, file), Path.Combine(RoutePath, "temp"));
                    }
                    catch { }
                    finally
                    {*/
                        count++;
                    //}
                }

                if (count > 0)
                {
                    await _GetDependencies();

                    await Task.Run(() => { DeleteDirectory(Path.Combine(RoutePath, "temp")); });
                }
            }
        }

        private long CountAllFiles()
        {
            long size = 0;

            bool apChanged = false;

            if (IsAP)
            {
                apChanged = GetDirectoryMD5(RoutePath, true) != SavedRoute.APChecksum;
            }

            bool loftsChanged = GetDirectoryMD5(Path.Combine(RoutePath, "Networks", "Loft Tiles")) != SavedRoute.LoftChecksum;
            bool roadsChanged = GetDirectoryMD5(Path.Combine(RoutePath, "Networks", "Road Tiles")) != SavedRoute.RoadChecksum;
            bool tracksChanged = GetDirectoryMD5(Path.Combine(RoutePath, "Networks", "Track Tiles")) != SavedRoute.TrackChecksum;
            bool sceneryChanged = GetDirectoryMD5(Path.Combine(RoutePath, "Scenery")) != SavedRoute.SceneryChecksum;
            bool rpChanged = GetFileMD5(Path.Combine(RoutePath, "RouteProperties.xml")) != SavedRoute.RoutePropertiesChecksum;
            bool scenariosChanged = GetDirectoryMD5(Path.Combine(RoutePath, "Scenarios")) != SavedRoute.ScenariosChecksum;

            if (!IsAP)
            {
                if (rpChanged)
                    size += GetFileSize(Path.Combine(RoutePath, "RouteProperties.xml"));

                foreach (string dir in Directory.GetDirectories(RoutePath))
                {
                    switch (Path.GetFileName(dir))
                    {
                        case "Networks":
                            if (loftsChanged || roadsChanged || tracksChanged)
                            {
                                size += GetDirectorySize(Path.Combine(dir, "Loft Tiles"), "*.*bin");
                                size += GetDirectorySize(Path.Combine(dir, "Road Tiles"), "*.*bin");
                                size += GetDirectorySize(Path.Combine(dir, "Track Tiles"), "*.*bin");
                            }

                            break;
                        case "Scenarios":
                            if (scenariosChanged)
                            {
                                foreach (string dir2 in Directory.GetDirectories(dir))
                                {
                                    size += GetDirectorySize(dir2, "*.*bin");
                                    size += GetFileSize(Path.Combine(dir2, "ScenarioProperties.xml"));
                                }
                            }
                            break;
                        case "Scenery":
                            if (sceneryChanged)
                                size += GetDirectorySize(dir, "*.*bin");
                            break;
                    }
                }
            } 
            else if (apChanged)
            {
                foreach (string file in Directory.GetFiles(RoutePath, "*.ap"))
                {
                    try
                    {
                        ZipFile.ExtractToDirectory(Path.Combine(RoutePath, file), Path.Combine(RoutePath, "temp"));
                    }
                    catch { }
                }

                size += GetFileSize(Path.Combine(RoutePath, "temp", "RouteProperties.xml"));
                foreach (string dir in Directory.GetDirectories(Path.Combine(RoutePath, "temp")))
                {
                    switch (Path.GetFileName(dir))
                    {
                        case "Networks":
                            foreach (string dir2 in Directory.GetDirectories(dir))
                            {
                                size += GetDirectorySize(dir2, "*.*bin");
                            }
                            break;
                        case "Scenarios":
                            foreach (string dir2 in Directory.GetDirectories(dir))
                            {
                                size += GetDirectorySize(dir2, "*.*bin");
                                size += GetFileSize(Path.Combine(dir2, "ScenarioProperties.xml"));
                            }
                            break;
                        case "Scenery":
                            size += GetDirectorySize(dir, "*.*bin");
                            break;
                    }
                }

                size += GetFileSize(Path.Combine(RoutePath, "RouteProperties.xml"));
                foreach (string dir in Directory.GetDirectories(RoutePath))
                {
                    switch (Path.GetFileName(dir))
                    {
                        case "Networks":
                            foreach (string dir2 in Directory.GetDirectories(dir))
                            {
                                size += GetDirectorySize(dir2, "*.*bin");
                            }
                            break;
                        case "Scenarios":
                            foreach (string dir2 in Directory.GetDirectories(dir))
                            {
                                size += GetDirectorySize(dir2, "*.*bin");
                                size += GetFileSize(Path.Combine(dir2, "ScenarioProperties.xml"));
                            }
                            break;
                        case "Scenery":
                            size += GetDirectorySize(dir, "*.*bin");
                            break;
                    }
                }
            }

            return size;
        }

        private static void DeleteDirectory(string directory)
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

        private long GetDirectorySize(string directory, string mask)
        {
            long size = 0;
            
            foreach (var file in Directory.GetFiles(directory, mask, SearchOption.AllDirectories))
            {
                size += GetFileSize(file);
            }

            return size;
        }

        private long GetFileSize(string file)
        {
            if (File.Exists(file))
            {
                FileInfo fi = new FileInfo(file);
                return fi.Length;
            }
            else
                return 0;

        }

        private string GetFileMD5(string path)
        {
            if (!File.Exists(path))
                return null;

            using (var md5 = MD5.Create())
            {
                byte[] pathBytes = Encoding.UTF8.GetBytes(path);
                md5.TransformBlock(pathBytes, 0, pathBytes.Length, pathBytes, 0);

                byte[] contentBytes = File.ReadAllBytes(path);
                md5.TransformBlock(contentBytes, 0, contentBytes.Length, contentBytes, 0);

                md5.TransformFinalBlock(new byte[0], 0, 0);

                return BitConverter.ToString(md5.Hash).Replace("-", "").ToLower();
            }
        }

        private string GetDirectoryMD5(string path, bool isAP = false)
        {
            if (!Directory.Exists(path))
                return null;
            
            var filePaths = isAP ? Directory.GetFiles(path, "*.ap").OrderBy(p => p).ToArray() : Directory.GetFiles(path, "*.*bin").OrderBy(p => p).ToArray();

            using (var md5 = MD5.Create())
            {
                foreach (var filePath in filePaths)
                {
                    byte[] pathBytes = Encoding.UTF8.GetBytes(filePath);
                    md5.TransformBlock(pathBytes, 0, pathBytes.Length, pathBytes, 0);

                    byte[] contentBytes = File.ReadAllBytes(filePath);
                    md5.TransformBlock(contentBytes, 0, contentBytes.Length, contentBytes, 0);
                }

                md5.TransformFinalBlock(new byte[0], 0, 0);
                return BitConverter.ToString(md5.Hash).Replace("-", "").ToLower();
            }
        }
    }
}
