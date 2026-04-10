using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Text;
using System.Threading.Tasks;
using CleanSweep.Models;

namespace CleanSweep.Services
{
    public class ScannerService
    {
        private static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
            { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".webp", ".tiff", ".tif" };

        // Folders that should never be traversed by any scan
        private static readonly string[] _systemSkipPrefixes = BuildSystemSkip();
        private static string[] BuildSystemSkip()
        {
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var list = new List<string>
            {
                win,
                Path.Combine(win, "System32"),
                Path.Combine(win, "SysWOW64"),
                Path.Combine(win, "WinSxS"),
                Path.Combine(win, "servicing"),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            };
            // WindowsApps lives beside Program Files
            var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(pf))
                list.Add(Path.Combine(Path.GetDirectoryName(pf) ?? pf, "WindowsApps"));
            return list.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        private static bool IsSystemPath(string path)
        {
            foreach (var s in _systemSkipPrefixes)
                if (path.StartsWith(s, StringComparison.OrdinalIgnoreCase))
                    return true;
            var name = Path.GetFileName(path);
            return string.Equals(name, "System Volume Information", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "$Recycle.Bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "$WinREAgent", StringComparison.OrdinalIgnoreCase);
        }

        public event Action<int, string>? OnProgress;

        /// <summary>Returns roots of all ready fixed drives on the machine.</summary>
        public static List<string> GetDefaultScanDirs()
        {
            var dirs = DriveInfo.GetDrives()
                .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
                .Select(d => d.RootDirectory.FullName)
                .ToList();

            if (dirs.Count == 0)
                dirs.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

            return dirs;
        }

        public static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} Б";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} КБ";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} МБ";
            return $"{bytes / (1024.0 * 1024 * 1024):F2} ГБ";
        }

        private static string? ComputeMD5(string path)
        {
            try
            {
                using var s = File.OpenRead(path);
                using var md5 = MD5.Create();
                return BitConverter.ToString(md5.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
            }
            catch { return null; }
        }

        private FileItem? MakeFileItem(string path)
        {
            try
            {
                var fi = new FileInfo(path);
                return new FileItem
                {
                    FullPath = fi.FullName, FileName = fi.Name, Directory = fi.DirectoryName ?? "",
                    Size = fi.Length, SizeHuman = FormatSize(fi.Length),
                    Modified = fi.LastWriteTime.ToString("dd.MM.yyyy HH:mm"),
                    Extension = fi.Extension.ToLowerInvariant(),
                };
            }
            catch { return null; }
        }

        private IEnumerable<string> SafeFiles(string dir, int maxDepth = 50)
        {
            var stack = new Stack<(string p, int d)>();
            stack.Push((dir, 0));
            while (stack.Count > 0)
            {
                var (cur, depth) = stack.Pop();
                if (depth > maxDepth) continue;
                string[] files;
                try { files = Directory.GetFiles(cur); } catch { files = Array.Empty<string>(); }
                foreach (var f in files) yield return f;
                string[] dirs;
                try { dirs = Directory.GetDirectories(cur); } catch { dirs = Array.Empty<string>(); }
                foreach (var d in dirs)
                {
                    try
                    {
                        if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                        if (IsSystemPath(d)) continue;
                        stack.Push((d, depth + 1));
                    }
                    catch { }
                }
            }
        }

        // ─── Duplicate Images (pHash) ────────────────────────────

        public async Task<List<DuplicateGroup>> ScanDuplicateImagesAsync(
            List<string> directories, int threshold, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                var imgList = new List<string>();
                foreach (var dir in directories)
                {
                    OnProgress?.Invoke(1, $"Поиск изображений: {dir}");
                    imgList.AddRange(SafeFiles(dir).Where(f => ImageExts.Contains(Path.GetExtension(f))));
                }
                var imgs = imgList;
                if (imgs.Count == 0) { OnProgress?.Invoke(100, "Изображения не найдены"); return new List<DuplicateGroup>(); }
                OnProgress?.Invoke(5, $"Найдено {imgs.Count:N0} изображений. Хеширование...");

                var hashes = new ConcurrentBag<(string path, ulong hash)>();
                int done = 0, total = imgs.Count;
                Parallel.ForEach(imgs, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, file =>
                {
                    try { hashes.Add((file, ImageHashService.ComputePhash(file))); } catch { }
                    int d = Interlocked.Increment(ref done);
                    if (d % 50 == 0 || d == total)
                        OnProgress?.Invoke(5 + (int)((double)d / total * 75), $"Хеширование: {d}/{total}...");
                });
                ct.ThrowIfCancellationRequested();

                // Cap at 8 000 — O(n²) stays under 32M comparisons; sort newest-first
                const int MaxImages   = 8_000;
                const int MaxGroups   = 200;
                const int MaxPerGroup = 20;
                var list = hashes
                    .OrderByDescending(h => { try { return new FileInfo(h.path).LastWriteTimeUtc; } catch { return DateTime.MinValue; } })
                    .Take(MaxImages)
                    .ToList();

                int n = list.Count;
                OnProgress?.Invoke(85, $"Группировка {n:N0} изображений...");

                var parent = new int[n];
                for (int k = 0; k < n; k++) parent[k] = k;

                int Find(int x) { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; }
                void Union(int a, int b) { parent[Find(a)] = Find(b); }

                // Parallel O(n²): each thread batches its matches locally, then locks once per row.
                // Avoids ConcurrentBag growing to millions of entries on photo-heavy libraries.
                var lockObj = new object();
                int processed = 0;
                Parallel.For(0, n - 1, new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount }, i =>
                {
                    ulong hi = list[i].hash;
                    List<(int, int)>? batch = null;
                    for (int j = i + 1; j < n; j++)
                        if (ImageHashService.HammingDistance(hi, list[j].hash) <= threshold)
                        {
                            batch ??= new List<(int, int)>();
                            batch.Add((i, j));
                        }
                    if (batch != null)
                        lock (lockObj) { foreach (var (a, b) in batch) Union(a, b); }
                    int p = Interlocked.Increment(ref processed);
                    if (p % 200 == 0)
                        OnProgress?.Invoke(85 + (int)((double)p / n * 13), $"Группировка: {p:N0}/{n:N0}...");
                });

                ct.ThrowIfCancellationRequested();
                OnProgress?.Invoke(98, "Формирование групп...");

                var buckets = new Dictionary<int, List<int>>();
                for (int k = 0; k < n; k++)
                {
                    int root = Find(k);
                    if (!buckets.ContainsKey(root)) buckets[root] = new();
                    buckets[root].Add(k);
                }

                // Sort largest groups first, cap both group count and size per group
                // to prevent WPF from rendering thousands of UI elements at once.
                var groups = new List<DuplicateGroup>(); int gid = 0;
                foreach (var bucket in buckets.Values.Where(b => b.Count >= 2)
                                                     .OrderByDescending(b => b.Count)
                                                     .Take(MaxGroups))
                {
                    gid++;
                    var files = new List<FileItem>();
                    foreach (var idx in bucket.Take(MaxPerGroup))
                    {
                        var item = MakeFileItem(list[idx].path);
                        if (item != null) { item.GroupId = gid; files.Add(item); }
                    }
                    // Best quality = largest file; tiebreaker = newest
                    files.Sort((a, b) =>
                    {
                        int cmp = b.Size.CompareTo(a.Size);
                        return cmp != 0 ? cmp : string.Compare(b.Modified, a.Modified, StringComparison.Ordinal);
                    });
                    if (files.Count >= 2)
                    {
                        files[0].IsOriginal = true;  files[0].IsSelected = false;
                        for (int fi = 1; fi < files.Count; fi++) { files[fi].IsOriginal = false; files[fi].IsSelected = true; }
                        groups.Add(new DuplicateGroup { Id = gid, Files = files, TotalSize = files.Sum(f => f.Size), TotalSizeHuman = FormatSize(files.Sum(f => f.Size)), Similarity = "pHash (визуальное сходство)" });
                    }
                }

                OnProgress?.Invoke(100, $"Найдено {groups.Count} групп дубликатов");
                return groups;
            }, ct);
        }

        // ─── Duplicate Files (MD5) ───────────────────────────────

        public async Task<List<DuplicateGroup>> ScanDuplicateFilesAsync(
            List<string> directories, long minSize, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                var sizeMap = new Dictionary<long, List<string>>(); int fc = 0;
                foreach (var dir in directories)
                {
                    OnProgress?.Invoke(1, $"Индексирование: {dir}");
                    foreach (var f in SafeFiles(dir))
                    {
                        ct.ThrowIfCancellationRequested();
                        try { long sz = new FileInfo(f).Length; if (sz >= minSize) { if (!sizeMap.ContainsKey(sz)) sizeMap[sz] = new(); sizeMap[sz].Add(f); fc++; } } catch { }
                        if (fc % 2000 == 0 && fc > 0)
                            OnProgress?.Invoke(Math.Min((int)((double)fc / 200000 * 18) + 1, 19),
                                $"Проиндексировано {fc:N0} файлов...");
                    }
                }
                OnProgress?.Invoke(20, $"Проиндексировано {fc:N0} файлов, ищем совпадения...");

                var cands = sizeMap.Where(kv => kv.Value.Count >= 2).ToList();
                int totalC = cands.Sum(c => c.Value.Count);
                var hashMap = new Dictionary<string, List<FileItem>>(); int proc = 0;
                foreach (var (sz, paths) in cands)
                    foreach (var path in paths)
                    {
                        ct.ThrowIfCancellationRequested();
                        var md5 = ComputeMD5(path);
                        if (md5 != null) { var item = MakeFileItem(path); if (item != null) { item.Hash = md5; if (!hashMap.ContainsKey(md5)) hashMap[md5] = new(); hashMap[md5].Add(item); } }
                        if (++proc % 100 == 0) OnProgress?.Invoke(20 + (int)((double)proc / Math.Max(totalC, 1) * 70), $"Хеширование: {proc}/{totalC}...");
                    }
                var groups = new List<DuplicateGroup>(); int gid = 0;
                foreach (var (h, files) in hashMap)
                {
                    if (files.Count < 2) continue; gid++;
                    // Largest file = original (best quality); tiebreaker = newest
                    files.Sort((a, b) =>
                    {
                        int cmp = b.Size.CompareTo(a.Size);
                        return cmp != 0 ? cmp : string.Compare(b.Modified, a.Modified, StringComparison.Ordinal);
                    });
                    files[0].GroupId = gid; files[0].IsOriginal = true; files[0].IsSelected = false;
                    for (int fi = 1; fi < files.Count; fi++) { files[fi].GroupId = gid; files[fi].IsOriginal = false; files[fi].IsSelected = true; }
                    groups.Add(new DuplicateGroup { Id = gid, Files = files, TotalSize = files.Sum(f => f.Size), TotalSizeHuman = FormatSize(files.Sum(f => f.Size)), Similarity = "MD5 (точная копия)" });
                }
                OnProgress?.Invoke(100, $"Найдено {groups.Count} групп, {groups.Sum(g => g.Files.Count - 1)} дубликатов");
                return groups;
            }, ct);
        }

        // ─── Junk Scanner ────────────────────────────────────────

        public async Task<List<JunkCategory>> ScanJunkAsync(CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string temp = Path.GetTempPath();
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

                // Firefox: enumerate profiles dynamically
                var ffDirs = new List<string>();
                var ffProfiles = Path.Combine(appData, "Mozilla", "Firefox", "Profiles");
                if (Directory.Exists(ffProfiles))
                    foreach (var p in Directory.GetDirectories(ffProfiles))
                    {
                        ffDirs.Add(Path.Combine(p, "cache2"));
                        ffDirs.Add(Path.Combine(p, "thumbnails"));
                    }

                var categories = new (string name, string iconKey, string[] dirs)[]
                {
                    ("Временные файлы", "IconTrash", new[] {
                        temp,
                        Path.Combine(win, "Temp"),
                    }),
                    ("Кэш браузеров", "IconDisk", new[] {
                        Path.Combine(local, "Google",          "Chrome",         "User Data", "Default", "Cache"),
                        Path.Combine(local, "Google",          "Chrome",         "User Data", "Default", "Code Cache"),
                        Path.Combine(local, "Microsoft",       "Edge",           "User Data", "Default", "Cache"),
                        Path.Combine(local, "BraveSoftware",   "Brave-Browser",  "User Data", "Default", "Cache"),
                        Path.Combine(local, "Vivaldi",         "User Data",      "Default", "Cache"),
                    }.Concat(ffDirs).ToArray()),
                    ("Discord и мессенджеры", "IconFile", new[] {
                        Path.Combine(appData, "discord",    "Cache"),
                        Path.Combine(appData, "Discord",    "Cache"),
                        Path.Combine(appData, "Slack",      "Cache"),
                        Path.Combine(appData, "Microsoft",  "Teams", "Cache"),
                    }),
                    ("Журналы и логи", "IconFile", new[] {
                        Path.Combine(win,   "Logs"),
                        Path.Combine(local, "CrashDumps"),
                        Path.Combine(local, "Microsoft", "Windows", "WER", "ReportQueue"),
                        Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive"),
                    }),
                    ("Миниатюры Windows", "IconCamera", new[] {
                        Path.Combine(local, "Microsoft", "Windows", "Explorer"),
                    }),
                    ("Кэш обновлений Windows", "IconPackage", new[] {
                        Path.Combine(win, "SoftwareDistribution", "Download"),
                        Path.Combine(win, "ServiceProfiles", "NetworkService", "AppData",
                            "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"),
                    }),
                    ("Недавние документы", "IconDisk", new[] {
                        Path.Combine(appData, "Microsoft", "Windows", "Recent"),
                        Path.Combine(appData, "Microsoft", "Windows", "Recent", "AutomaticDestinations"),
                        Path.Combine(appData, "Microsoft", "Windows", "Recent", "CustomDestinations"),
                    }),
                };

                // Recycle Bin: enumerate $Recycle.Bin\*\$R* on every fixed drive
                var recyclePaths = new List<string>();
                long recycleSz = 0; int recycleFC = 0;
                foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed))
                {
                    try
                    {
                        var binRoot = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
                        if (!Directory.Exists(binRoot)) continue;
                        // Each SID subfolder; $R* = actual content, $I* = metadata
                        foreach (var f in Directory.GetFiles(binRoot, "$R*", SearchOption.AllDirectories))
                        {
                            try
                            {
                                var fi = new FileInfo(f);
                                recycleSz += fi.Length; recycleFC++;
                                if (recyclePaths.Count < 5000) recyclePaths.Add(f);
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                var result = new List<JunkCategory>();
                if (recycleFC > 0)
                    result.Add(new JunkCategory
                    {
                        Id = 0, Name = "Корзина Windows", IconKey = "IconTrash",
                        FileCount = recycleFC, TotalSize = recycleSz,
                        TotalSizeHuman = FormatSize(recycleSz), FilePaths = recyclePaths
                    });

                int step = 100 / Math.Max(categories.Length, 1);
                for (int i = 0; i < categories.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var (name, iconKey, dirs) = categories[i];
                    OnProgress?.Invoke(i * step, $"Сканирование: {name}...");
                    long totalSz = 0; int fc = 0; var paths = new List<string>();
                    foreach (var dir in dirs)
                    {
                        if (!Directory.Exists(dir)) continue;
                        foreach (var f in SafeFiles(dir, 5))
                        {
                            try { totalSz += new FileInfo(f).Length; fc++; if (paths.Count < 5000) paths.Add(f); } catch { }
                        }
                    }
                    result.Add(new JunkCategory { Id = i + 1, Name = name, IconKey = iconKey, FileCount = fc, TotalSize = totalSz, TotalSizeHuman = FormatSize(totalSz), FilePaths = paths });
                }
                OnProgress?.Invoke(100, $"Найдено {result.Sum(c => c.FileCount):N0} файлов ({FormatSize(result.Sum(c => c.TotalSize))})");
                return result;
            }, ct);
        }

        // ─── Large Files ─────────────────────────────────────────

        public async Task<List<FileItem>> ScanLargeFilesAsync(
            List<string> directories, long minSizeMb, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                long minB = minSizeMb * 1024 * 1024;
                var large = new List<FileItem>(); int sc = 0;
                foreach (var dir in directories)
                {
                    OnProgress?.Invoke(1, $"Сканирование: {dir}");
                    foreach (var f in SafeFiles(dir))
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            var fi = new FileInfo(f); sc++;
                            if (fi.Length >= minB)
                                large.Add(new FileItem
                                {
                                    FullPath = fi.FullName, FileName = fi.Name,
                                    Directory = fi.DirectoryName ?? "",
                                    Size = fi.Length, SizeHuman = FormatSize(fi.Length),
                                    Modified = fi.LastWriteTime.ToString("dd.MM.yyyy HH:mm"),
                                    Extension = fi.Extension.ToLowerInvariant()
                                });
                        }
                        catch { }
                        if (sc % 1000 == 0)
                            OnProgress?.Invoke(
                                Math.Min((int)((double)sc / 500000 * 90), 90),
                                $"Проверено {sc:N0} файлов, найдено {large.Count} крупных...");
                    }
                }
                large.Sort((a, b) => b.Size.CompareTo(a.Size));
                if (large.Count > 500) large = large.Take(500).ToList();
                OnProgress?.Invoke(100, $"Найдено {large.Count} файлов > {minSizeMb} МБ");
                return large;
            }, ct);
        }

        public static (int deleted, int errors) DeleteFiles(IEnumerable<string> paths)
        {
            int del = 0, err = 0;
            foreach (var p in paths) { try { if (File.Exists(p)) { File.Delete(p); del++; } } catch { err++; } }
            return (del, err);
        }

        // ─── Disk Usage Analyzer ─────────────────────────────────────────────
        public async Task<List<FolderSizeItem>> ScanDiskUsageAsync(
            List<string> roots, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                var result = new ConcurrentBag<FolderSizeItem>();
                var topDirs = new List<string>();
                foreach (var root in roots)
                {
                    try { topDirs.AddRange(Directory.GetDirectories(root)); } catch { }
                }

                int total = Math.Max(topDirs.Count, 1), done = 0;
                Parallel.ForEach(topDirs,
                    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                    dir =>
                    {
                        long size = 0; int fc = 0;
                        foreach (var f in SafeFiles(dir, 30))
                        {
                            try { size += new FileInfo(f).Length; fc++; } catch { }
                        }
                        if (size > 0)
                            result.Add(new FolderSizeItem
                            {
                                FullPath  = dir,
                                Name      = Path.GetFileName(dir) is { Length: > 0 } n ? n : dir,
                                Size      = size,
                                SizeHuman = FormatSize(size),
                                FileCount = fc
                            });
                        int d = Interlocked.Increment(ref done);
                        if (d % 5 == 0)
                            OnProgress?.Invoke(5 + (int)((double)d / total * 88),
                                $"Анализ: {Path.GetFileName(dir)}...");
                    });

                var list = result.OrderByDescending(f => f.Size).Take(30).ToList();
                if (list.Count > 0)
                {
                    long max = list[0].Size;
                    foreach (var item in list)
                        item.RelativeSize = max > 0 ? (double)item.Size / max : 0;
                }
                OnProgress?.Invoke(100, $"Проанализировано {list.Count} папок");
                return list;
            }, ct);
        }

        // ─── Empty Folder Finder ─────────────────────────────────────────────
        public async Task<List<EmptyFolderItem>> ScanEmptyFoldersAsync(
            List<string> dirs, CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                var result = new List<EmptyFolderItem>();
                var stack  = new Stack<string>();
                foreach (var d in dirs) stack.Push(d);

                int scanned = 0;
                while (stack.Count > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    var cur = stack.Pop();
                    if (IsSystemPath(cur)) continue;
                    scanned++;
                    if (scanned % 500 == 0)
                        OnProgress?.Invoke(Math.Min(scanned / 200, 90),
                            $"Проверено {scanned:N0} папок...");

                    string[] subDirs, files;
                    try { subDirs = Directory.GetDirectories(cur); } catch { continue; }
                    try { files   = Directory.GetFiles(cur);        } catch { continue; }

                    if (files.Length == 0 && subDirs.Length == 0)
                    {
                        result.Add(new EmptyFolderItem
                        {
                            FullPath = cur,
                            Name     = Path.GetFileName(cur),
                            Parent   = Path.GetDirectoryName(cur) ?? ""
                        });
                    }
                    else
                    {
                        foreach (var sub in subDirs)
                        {
                            try
                            {
                                if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) != 0) continue;
                                if (IsSystemPath(sub)) continue;
                                stack.Push(sub);
                            }
                            catch { }
                        }
                    }
                }

                if (result.Count > 2000) result = result.Take(2000).ToList();
                OnProgress?.Invoke(100, $"Найдено {result.Count} пустых папок");
                return result;
            }, ct);
        }

        // ─── Startup Manager ─────────────────────────────────────────────────
        public async Task<List<StartupItem>> ScanStartupAsync(CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                var result = new List<StartupItem>();
                OnProgress?.Invoke(20, "Чтение реестра...");

                void ReadKey(Microsoft.Win32.RegistryKey? hive, string path, string source)
                {
                    if (hive == null) return;
                    try
                    {
                        using var key = hive.OpenSubKey(path);
                        if (key == null) return;
                        foreach (var name in key.GetValueNames())
                        {
                            var cmd = key.GetValue(name)?.ToString() ?? "";
                            result.Add(new StartupItem
                            {
                                Name = name, Command = cmd, Source = source,
                                RegistryKey = source + "\\" + path, IsEnabled = true
                            });
                        }
                    }
                    catch { }
                }

                const string runPath   = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
                const string runPath32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";

                ReadKey(Microsoft.Win32.Registry.CurrentUser,  runPath,   "HKCU");
                ReadKey(Microsoft.Win32.Registry.LocalMachine, runPath,   "HKLM");
                ReadKey(Microsoft.Win32.Registry.LocalMachine, runPath32, "HKLM (32-bit)");

                try
                {
                    using var approved = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run");
                    if (approved != null)
                        foreach (var name in approved.GetValueNames())
                        {
                            if (approved.GetValue(name) is byte[] b && b.Length > 0 && b[0] == 3)
                            {
                                var item = result.FirstOrDefault(r => r.Name == name);
                                if (item != null) item.IsEnabled = false;
                            }
                        }
                }
                catch { }

                OnProgress?.Invoke(100, $"Найдено {result.Count} записей автозапуска");
                return result;
            }, ct);
        }

        public static void DeleteStartupItem(StartupItem item)
        {
            try
            {
                var hive    = item.RegistryKey.StartsWith("HKCU")
                    ? Microsoft.Win32.Registry.CurrentUser
                    : Microsoft.Win32.Registry.LocalMachine;
                var keyPath = string.Join("\\", item.RegistryKey.Split('\\').Skip(1));
                using var key = hive.OpenSubKey(keyPath, writable: true);
                key?.DeleteValue(item.Name, throwOnMissingValue: false);
            }
            catch { }
        }

        public static (int deleted, int errors) DeleteFolders(IEnumerable<string> paths)
        {
            int del = 0, err = 0;
            foreach (var p in paths)
            {
                try { if (Directory.Exists(p)) { Directory.Delete(p, recursive: false); del++; } }
                catch { err++; }
            }
            return (del, err);
        }

        // ─── Broken Shortcut Finder ──────────────────────────────────────────
        // Parses .lnk binary format (MS-SHLLINK) to extract the target path
        // without requiring COM/STA — works on any thread.
        private static string? ResolveLnkTarget(string lnkPath)
        {
            try
            {
                var b = File.ReadAllBytes(lnkPath);
                // HeaderSize must be 0x4C
                if (b.Length < 76 || BitConverter.ToUInt32(b, 0) != 0x4C) return null;

                uint flags      = BitConverter.ToUInt32(b, 0x14);
                bool hasIDList  = (flags & 0x0001) != 0;
                bool hasLinkInfo= (flags & 0x0002) != 0;

                int pos = 76;
                if (hasIDList)
                {
                    if (pos + 2 > b.Length) return null;
                    pos += 2 + BitConverter.ToUInt16(b, pos);
                }
                if (!hasLinkInfo || pos + 28 > b.Length) return null;

                int li         = pos;                                // start of LinkInfo
                int liHdrSize  = BitConverter.ToInt32(b, li + 0);
                int liFlags    = BitConverter.ToInt32(b, li + 4);
                if ((liFlags & 1) == 0) return null;                 // no local path

                // Unicode path (present when LinkInfoHeaderSize >= 0x24)
                if (liHdrSize >= 0x24)
                {
                    int uOff = BitConverter.ToInt32(b, li + 0x18);
                    if (uOff > 0)
                    {
                        var sb = new StringBuilder();
                        int sp = li + uOff;
                        while (sp + 1 < b.Length)
                        {
                            char ch = (char)((b[sp + 1] << 8) | b[sp]);
                            if (ch == '\0') break;
                            sb.Append(ch); sp += 2;
                        }
                        if (sb.Length > 0) return sb.ToString();
                    }
                }

                // ANSI fallback (offset at li + 0x0C)
                int aOff = BitConverter.ToInt32(b, li + 0x0C);
                if (aOff <= 0) return null;
                int ap = li + aOff;
                if (ap >= b.Length) return null;
                int ep = ap;
                while (ep < b.Length && b[ep] != 0) ep++;
                return Encoding.Default.GetString(b, ap, ep - ap);
            }
            catch { return null; }
        }

        public async Task<List<ShortcutItem>> ScanBrokenShortcutsAsync(CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                string appData  = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string userData = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

                var scanDirs = new (string path, string label)[]
                {
                    (Path.Combine(userData, "Desktop"),                                              "Рабочий стол"),
                    (Path.Combine(progData, "Microsoft", "Windows", "Start Menu", "Programs"),       "Меню «Пуск»"),
                    (Path.Combine(appData,  "Microsoft", "Windows", "Start Menu", "Programs"),       "Меню «Пуск» (пользователь)"),
                    (Path.Combine(appData,  "Microsoft", "Windows", "Start Menu", "Programs", "Startup"), "Автозагрузка"),
                    (Path.Combine(appData,  "Microsoft", "Windows", "SendTo"),                       "Отправить"),
                    (Path.Combine(appData,  "Microsoft", "Internet Explorer", "Quick Launch"),       "Быстрый запуск"),
                    (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),    "Общий рабочий стол"),
                };

                var result = new List<ShortcutItem>();
                int step = 0, total = scanDirs.Length;
                foreach (var (dir, label) in scanDirs)
                {
                    ct.ThrowIfCancellationRequested();
                    OnProgress?.Invoke(5 + (int)((double)step / total * 85), $"Проверка: {label}...");
                    step++;
                    if (!Directory.Exists(dir)) continue;

                    string[] lnkFiles;
                    try { lnkFiles = Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories); }
                    catch { continue; }

                    foreach (var lnk in lnkFiles)
                    {
                        ct.ThrowIfCancellationRequested();
                        var target = ResolveLnkTarget(lnk);
                        if (target == null || target.Trim().Length == 0) continue; // unresolvable — skip
                        // Only report if the target is a local file/dir path that no longer exists
                        if (!target.StartsWith("\\\\", StringComparison.Ordinal) &&   // skip UNC/network
                            !File.Exists(target) && !Directory.Exists(target))
                        {
                            result.Add(new ShortcutItem
                            {
                                ShortcutPath = lnk,
                                ShortcutName = Path.GetFileNameWithoutExtension(lnk),
                                TargetPath   = target,
                                Location     = label,
                                IsSelected   = true
                            });
                        }
                    }
                }

                OnProgress?.Invoke(100, $"Найдено {result.Count} сломанных ярлыков");
                return result;
            }, ct);
        }
    }
}
