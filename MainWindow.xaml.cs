using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using CleanSweep.Models;
using CleanSweep.Services;

namespace CleanSweep
{
    public partial class MainWindow : Window
    {
        // ── Update check ─────────────────────────────────────────────────
        // Set to your GitHub repo ("username/reponame") to enable auto-updates.
        // Leave empty to disable.
        private const string GitHubRepo  = "asintiko/CleanSweep";
        private const string AppVersion  = "1.0.0";
        private string? _releaseUrl;

        private readonly ScannerService _scanner = new();
        private CancellationTokenSource? _cts;
        private string _currentTab = "dup_images";

        // Selected scan folders; null = scan all fixed drives (default)
        private List<string>? _scanFolders;

        // Limit concurrent thumbnail IO so we don't launch hundreds of Tasks at once
        private static readonly SemaphoreSlim _thumbSem = new(4, 4);

        private static bool IsImageFile(string path) =>
            new[] { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".webp", ".heic" }
            .Contains(Path.GetExtension(path).ToLowerInvariant());

        private List<DuplicateGroup>? _dupImageResults;
        private List<DuplicateGroup>? _dupFileResults;
        private List<JunkCategory>?  _junkResults;
        private List<FileItem>?      _largeResults;
        private List<FolderSizeItem>?   _diskResults;
        private List<EmptyFolderItem>?  _emptyResults;
        private List<StartupItem>?      _startupResults;
        private List<ShortcutItem>?     _shortcutsResults;

        // ── UI color palette (matches App.xaml) ──────────────────────────
        static readonly Brush CardBg       = new SolidColorBrush(Color.FromRgb(255, 255, 255));
        static readonly Brush BorderC      = new SolidColorBrush(Color.FromRgb(208, 215, 222));
        static readonly Brush AccentC      = new SolidColorBrush(Color.FromRgb(9, 105, 218));
        static readonly Brush AccentLightC = new SolidColorBrush(Color.FromRgb(221, 244, 255));
        static readonly Brush DangerC      = new SolidColorBrush(Color.FromRgb(207, 34, 46));
        static readonly Brush DangerLightC = new SolidColorBrush(Color.FromRgb(255, 235, 233));
        static readonly Brush WarningC     = new SolidColorBrush(Color.FromRgb(154, 103, 0));
        static readonly Brush WarningLightC= new SolidColorBrush(Color.FromRgb(255, 248, 197));
        static readonly Brush Text1        = new SolidColorBrush(Color.FromRgb(31,  35, 40));
        static readonly Brush Text2        = new SolidColorBrush(Color.FromRgb(99, 108, 118));
        static readonly Brush TextDim      = new SolidColorBrush(Color.FromRgb(132, 141, 151));
        static readonly Brush TextMut      = new SolidColorBrush(Color.FromRgb(145, 152, 161));

        // sidebar disk-info colors (dark bg)
        static readonly Brush SideText  = new SolidColorBrush(Color.FromRgb(0xCD, 0xD9, 0xE5));
        static readonly Brush SideMuted = new SolidColorBrush(Color.FromRgb(0x76, 0x83, 0x90));
        static readonly Brush SideBar   = new SolidColorBrush(Color.FromRgb(0x30, 0x36, 0x3D));
        static readonly Brush SideAccent= new SolidColorBrush(Color.FromRgb(0x2F, 0x81, 0xF7));

        public MainWindow()
        {
            InitializeComponent();
            _scanner.OnProgress += (p, l) => Dispatcher.BeginInvoke(() => UpdateProgress(p, l));
            LoadDiskInfo();
            RefreshFolderChips();
            _ = CheckForUpdatesAsync();
        }

        // ─── Navigation ──────────────────────────────────────────────────

        private void NavChanged(object sender, RoutedEventArgs e)
        {
            if (sender is RadioButton rb && rb.Tag is string tag)
            {
                _currentTab = tag;
                UpdateTabUI();
                ShowCached();
            }
        }

        private readonly Dictionary<string, string> _titles = new()
        {
            ["dup_images"] = "Дубликаты фото",
            ["dup_files"]  = "Дубликаты файлов",
            ["junk"]       = "Очистка мусора",
            ["large"]      = "Большие файлы",
            ["disk"]       = "Анализ диска",
            ["empty"]      = "Пустые папки",
            ["startup"]    = "Автозапуск",
            ["shortcuts"]  = "Сломанные ярлыки",
        };
        private readonly Dictionary<string, string> _emptyTitles = new()
        {
            ["dup_images"] = "Найду одинаковые фотографии",
            ["dup_files"]  = "Найду точные копии файлов",
            ["junk"]       = "Уберу мусор с компьютера",
            ["large"]      = "Покажу самые тяжёлые файлы",
            ["disk"]       = "Анализ занятого места",
            ["empty"]      = "Найду пустые папки",
            ["startup"]    = "Программы автозапуска",
            ["shortcuts"]  = "Найду битые ярлыки",
        };
        private readonly Dictionary<string, string> _iconKeys = new()
        {
            ["dup_images"] = "IconCamera",
            ["dup_files"]  = "IconFile",
            ["junk"]       = "IconBroom",
            ["large"]      = "IconPackage",
            ["disk"]       = "IconBarChart",
            ["empty"]      = "IconFolder",
            ["startup"]    = "IconStartup",
            ["shortcuts"]  = "IconLink",
        };

        private void UpdateTabUI()
        {
            TabTitle.Text      = _titles.GetValueOrDefault(_currentTab, "");
            TxtEmptyTitle.Text = _emptyTitles.GetValueOrDefault(_currentTab, "");
            var geoKey = _iconKeys.GetValueOrDefault(_currentTab, "IconCamera");
            if (FindResource(geoKey) is Geometry g)
            {
                TabIcon.Data   = g;
                EmptyIcon.Data = g;
            }
            // Junk, startup and shortcuts use fixed/registry paths — folder selector doesn't apply
            FolderBar.Visibility = (_currentTab == "junk" || _currentTab == "startup" || _currentTab == "shortcuts")
                ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ShowCached()
        {
            switch (_currentTab)
            {
                case "dup_images" when _dupImageResults != null: ShowDupGroups(_dupImageResults); return;
                case "dup_files"  when _dupFileResults  != null: ShowDupGroups(_dupFileResults);  return;
                case "junk"       when _junkResults     != null: ShowJunk(_junkResults);           return;
                case "large"      when _largeResults    != null: ShowLarge(_largeResults);         return;
                case "disk"       when _diskResults     != null: ShowDiskAnalysis(_diskResults);   return;
                case "empty"      when _emptyResults    != null: ShowEmptyFolders(_emptyResults);  return;
                case "startup"    when _startupResults  != null: ShowStartup(_startupResults);     return;
                case "shortcuts"  when _shortcutsResults != null: ShowShortcuts(_shortcutsResults); return;
            }
            ShowEmpty();
        }

        private void ShowEmpty()
        {
            StopEmptyStateAnimation();
            EmptyState.Visibility   = Visibility.Visible;
            ResultsList.Visibility  = Visibility.Collapsed;
            SummaryPanel.Visibility = Visibility.Collapsed;
            SuccessState.Visibility = Visibility.Collapsed;
            BtnDelete.Visibility    = Visibility.Collapsed;
            StartEmptyStateAnimation();
        }

        private void ShowSuccessScreen(string title, string detail)
        {
            StopEmptyStateAnimation();
            SuccessState.Visibility = Visibility.Visible;
            TxtSuccess.Text         = title;
            TxtSuccessDetail.Text   = detail;
            ResultsList.Visibility  = Visibility.Collapsed;
            SummaryPanel.Visibility = Visibility.Collapsed;
            BtnDelete.Visibility    = Visibility.Collapsed;
            PlaySuccessBounce();
        }

        // ─── Scan ────────────────────────────────────────────────────────

        private async void OnScan(object sender, RoutedEventArgs e)
        {
            // For tabs that use user-selected paths, ask scope if no custom folders selected
            var fixedTabs = new HashSet<string> { "junk", "startup", "shortcuts" };
            if (!fixedTabs.Contains(_currentTab) && _scanFolders == null)
            {
                var result = MessageBox.Show(
                    "Выберите область сканирования:\n\n" +
                    "«Да» — выбрать конкретные папки\n" +
                    "«Нет» — сканировать весь компьютер",
                    "CleanSweep",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Cancel) return;
                if (result == MessageBoxResult.Yes)
                {
                    OnAddFolder(sender, e);
                    if (_scanFolders == null) return; // user cancelled the folder picker
                }
            }

            _cts = new CancellationTokenSource();
            StopEmptyStateAnimation();
            BtnScan.IsEnabled       = false;
            TxtScanLabel.Text       = "Сканирование...";
            BtnCancel.Visibility    = Visibility.Visible;
            BtnDelete.Visibility    = Visibility.Collapsed;
            EmptyState.Visibility   = Visibility.Collapsed;
            ResultsList.Visibility  = Visibility.Collapsed;
            SummaryPanel.Visibility = Visibility.Collapsed;
            SuccessState.Visibility = Visibility.Collapsed;
            ProgressPanel.Visibility = Visibility.Visible;
            // clear any leftover animation before setting value
            ProgressBarMain.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, null);
            ProgressBarMain.Value = 0;
            StartScanAnimation();

            var dirs = (_scanFolders is { Count: > 0 })
                ? _scanFolders
                : ScannerService.GetDefaultScanDirs();

            try
            {
                switch (_currentTab)
                {
                    case "dup_images":
                        _dupImageResults = await _scanner.ScanDuplicateImagesAsync(dirs, 6, _cts.Token);
                        ShowDupGroups(_dupImageResults);
                        break;
                    case "dup_files":
                        _dupFileResults = await _scanner.ScanDuplicateFilesAsync(dirs, 1024, _cts.Token);
                        ShowDupGroups(_dupFileResults);
                        break;
                    case "junk":
                        _junkResults = await _scanner.ScanJunkAsync(_cts.Token);
                        ShowJunk(_junkResults);
                        break;
                    case "large":
                        _largeResults = await _scanner.ScanLargeFilesAsync(dirs, 50, _cts.Token);
                        ShowLarge(_largeResults);
                        break;
                    case "disk":
                        _diskResults = await _scanner.ScanDiskUsageAsync(dirs, _cts.Token);
                        ShowDiskAnalysis(_diskResults);
                        break;
                    case "empty":
                        _emptyResults = await _scanner.ScanEmptyFoldersAsync(dirs, _cts.Token);
                        ShowEmptyFolders(_emptyResults);
                        break;
                    case "startup":
                        _startupResults = await _scanner.ScanStartupAsync(_cts.Token);
                        ShowStartup(_startupResults);
                        break;
                    case "shortcuts":
                        _shortcutsResults = await _scanner.ScanBrokenShortcutsAsync(_cts.Token);
                        ShowShortcuts(_shortcutsResults);
                        break;
                }
            }
            catch (OperationCanceledException) { ShowEmpty(); }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка сканирования:\n{ex.Message}", "CleanSweep",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                ShowEmpty();
            }
            finally
            {
                StopScanAnimation();
                BtnScan.IsEnabled        = true;
                TxtScanLabel.Text        = "Сканировать";
                BtnCancel.Visibility     = Visibility.Collapsed;
                ProgressPanel.Visibility = Visibility.Collapsed;
            }
        }

        private void OnCancel(object sender, RoutedEventArgs e) => _cts?.Cancel();

        private void StartScanAnimation()
        {
            var spin = new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.85)))
                { RepeatBehavior = RepeatBehavior.Forever };
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, spin);

            var pulse = new DoubleAnimation(1.0, 1.08, new Duration(TimeSpan.FromSeconds(0.75)))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                  EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            SpinnerScale.BeginAnimation(ScaleTransform.ScaleXProperty, pulse);
            SpinnerScale.BeginAnimation(ScaleTransform.ScaleYProperty, pulse);
        }

        private void StopScanAnimation()
        {
            SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            SpinnerScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            SpinnerScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }

        private void UpdateProgress(int p, string l)
        {
            TxtProgressPct.Text   = $"{p}%";
            TxtProgressLabel.Text = l;
            var anim = new DoubleAnimation(p, new Duration(TimeSpan.FromMilliseconds(280)))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            ProgressBarMain.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty, anim);
        }

        private static void FadeIn(UIElement el)
        {
            el.Opacity = 0;
            el.Visibility = Visibility.Visible;
            var anim = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(220)))
                { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            el.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        // Shows results list with slide-up stagger animation on items
        private void ShowResultsAnimated()
        {
            StopEmptyStateAnimation();
            EmptyState.Visibility   = Visibility.Collapsed;
            SuccessState.Visibility = Visibility.Collapsed;

            FadeIn(SummaryPanel);
            ResultsList.Visibility = Visibility.Visible;
            ResultsList.Opacity    = 1;

            // Apply stagger animation to individual result items
            if (ResultsList.ItemsSource is List<UIElement> items)
            {
                int delay = 0;
                foreach (var item in items)
                {
                    SlideUpIn(item, delay);
                    delay += 30; // 30ms stagger between cards
                    if (delay > 400) delay = 400; // cap so large lists don't wait forever
                }
            }
        }

        // Slide-up + fade for result cards with stagger
        private static void SlideUpIn(UIElement el, int delayMs = 0)
        {
            if (el.RenderTransform is not TranslateTransform)
                el.RenderTransform = new TranslateTransform(0, 18);
            else
                ((TranslateTransform)el.RenderTransform).Y = 18;

            el.Opacity = 0;

            var dur = new Duration(TimeSpan.FromMilliseconds(300));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var begin = TimeSpan.FromMilliseconds(delayMs);

            var fadeAnim = new DoubleAnimation(0, 1, dur) { BeginTime = begin, EasingFunction = ease };
            var slideAnim = new DoubleAnimation(18, 0, dur) { BeginTime = begin, EasingFunction = ease };

            el.BeginAnimation(UIElement.OpacityProperty, fadeAnim);
            ((TranslateTransform)el.RenderTransform).BeginAnimation(TranslateTransform.YProperty, slideAnim);
        }

        // Floating hover animation for empty-state icon
        private void StartEmptyStateAnimation()
        {
            var floatUp = new DoubleAnimation(0, -6, new Duration(TimeSpan.FromSeconds(1.5)))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                  EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            EmptyIconFloat.BeginAnimation(TranslateTransform.YProperty, floatUp);

            var breathe = new DoubleAnimation(1.0, 1.04, new Duration(TimeSpan.FromSeconds(2.0)))
                { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
                  EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
            EmptyIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, breathe);
            EmptyIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, breathe);
        }

        private void StopEmptyStateAnimation()
        {
            EmptyIconFloat.BeginAnimation(TranslateTransform.YProperty, null);
            EmptyIconScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            EmptyIconScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }

        // Success checkmark bounce (scale 0 → 1.15 → 1.0)
        private void PlaySuccessBounce()
        {
            var scaleUp = new DoubleAnimationUsingKeyFrames { Duration = new Duration(TimeSpan.FromMilliseconds(500)) };
            scaleUp.KeyFrames.Add(new EasingDoubleKeyFrame(0,    KeyTime.FromPercent(0)));
            scaleUp.KeyFrames.Add(new EasingDoubleKeyFrame(1.15, KeyTime.FromPercent(0.55),
                new CubicEase { EasingMode = EasingMode.EaseOut }));
            scaleUp.KeyFrames.Add(new EasingDoubleKeyFrame(1.0,  KeyTime.FromPercent(1.0),
                new CubicEase { EasingMode = EasingMode.EaseInOut }));

            SuccessScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleUp);
            SuccessScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleUp.Clone());
        }

        // ─── Photo Preview ───────────────────────────────────────────────

        private void OpenPreview(string path, string fileName, string sizeHuman, string modified)
        {
            PreviewFileName.Text = fileName;
            PreviewFileInfo.Text = $"{sizeHuman}  ·  {modified}";
            PreviewImage.Source  = null;
            PreviewLoadingText.Text       = "Загрузка...";
            PreviewLoadingText.Visibility = Visibility.Visible;
            FadeIn(PreviewOverlay);

            _ = Task.Run(() =>
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource      = new Uri(path);
                    bmp.DecodePixelWidth = 1200;
                    bmp.CacheOption    = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    Dispatcher.BeginInvoke(() =>
                    {
                        PreviewImage.Source           = bmp;
                        PreviewLoadingText.Visibility = Visibility.Collapsed;
                    });
                }
                catch
                {
                    Dispatcher.BeginInvoke(() => PreviewLoadingText.Text = "Не удалось загрузить");
                }
            });
        }

        private void ClosePreview()
        {
            PreviewOverlay.Visibility = Visibility.Collapsed;
            PreviewImage.Source       = null; // release memory
        }

        private void OnPreviewClose(object sender, RoutedEventArgs e) => ClosePreview();

        private void OnPreviewBackdropClick(object sender, MouseButtonEventArgs e)
        {
            if (e.Source == PreviewOverlay) ClosePreview();
        }

        private void OnPreviewCardClick(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true; // don't propagate to backdrop
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Escape && PreviewOverlay.Visibility == Visibility.Visible)
                ClosePreview();
        }

        // ─── Icon helper ─────────────────────────────────────────────────

        private System.Windows.Shapes.Path MakeIcon(string geoKey, Brush fill, double size = 16)
        {
            var path = new System.Windows.Shapes.Path
            {
                Stretch = Stretch.Uniform, Width = size, Height = size,
                Fill = fill, VerticalAlignment = VerticalAlignment.Center
            };
            if (FindResource(geoKey) is Geometry g) path.Data = g;
            return path;
        }

        // ─── Duplicate Groups ────────────────────────────────────────────

        private void ShowDupGroups(List<DuplicateGroup> groups)
        {
            EmptyState.Visibility   = Visibility.Collapsed;
            SuccessState.Visibility = Visibility.Collapsed;

            if (groups.Count == 0)
            {
                ShowSuccessScreen("Дубликаты не найдены", "Все файлы уникальны");
                return;
            }

            int  totalDup = groups.Sum(g => g.Files.Count - 1);
            long dupSz    = groups.Sum(g => g.Files.Where(f => !f.IsOriginal).Sum(f => f.Size));
            ShowSummary(
                ("Групп",            groups.Count.ToString(),                false),
                ("Дубликатов",       totalDup.ToString(),                    true),
                ("Можно освободить", ScannerService.FormatSize(dupSz),       true));

            var items = new List<UIElement>();
            foreach (var g in groups) items.Add(BuildGroup(g));
            ResultsList.ItemsSource = items;
            ShowResultsAnimated();
            TxtDeleteLabel.Text  = "Удалить выбранные";
            BtnDelete.Visibility = Visibility.Visible;
            UpdateCount();
        }

        // Builds a thumbnail Border with async image loading and click-to-preview
        private Border BuildThumb(FileItem file)
        {
            var thumb = new Border
            {
                Width = 52, Height = 52,
                CornerRadius = new CornerRadius(5),
                ClipToBounds = true,
                Margin = new Thickness(0, 0, 10, 0),
                Cursor = Cursors.Hand,
                Background = new SolidColorBrush(Color.FromRgb(234, 238, 242))
            };
            var timg = new Image { Stretch = Stretch.UniformToFill };
            thumb.Child = timg;

            var capPath = file.FullPath; var capFile = file;
            thumb.MouseLeftButtonUp += (s, ev) =>
            {
                ev.Handled = true;
                OpenPreview(capPath, capFile.FileName, capFile.SizeHuman, capFile.Modified);
            };
            _ = Task.Run(async () =>
            {
                await _thumbSem.WaitAsync();
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(capPath);
                    bmp.DecodePixelWidth = 104;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    Dispatcher.BeginInvoke(() => timg.Source = bmp);
                }
                catch { }
                finally { _thumbSem.Release(); }
            });
            return thumb;
        }

        // Builds a file info row (thumbnail + name/dir + size/date) for use in group cards
        private DockPanel BuildFileRow(FileItem file)
        {
            var dock = new DockPanel();

            if (IsImageFile(file.FullPath))
                dock.Children.Add(BuildThumb(file));

            var sz = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            DockPanel.SetDock(sz, Dock.Right);
            sz.Children.Add(new TextBlock { Text = file.SizeHuman, FontSize = 11, Foreground = Text2, HorizontalAlignment = HorizontalAlignment.Right });
            sz.Children.Add(new TextBlock { Text = file.Modified,  FontSize = 9,  Foreground = TextMut, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 1, 0, 0) });
            dock.Children.Add(sz);

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock { Text = file.FileName,  FontSize = 12, FontWeight = FontWeights.Medium, Foreground = Text1, TextTrimming = TextTrimming.CharacterEllipsis });
            info.Children.Add(new TextBlock { Text = file.Directory, FontSize = 10, Foreground = TextMut, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
            dock.Children.Add(info);

            return dock;
        }

        private Border BuildGroup(DuplicateGroup group)
        {
            var card = new Border
            {
                CornerRadius    = new CornerRadius(8),
                Background      = CardBg,
                BorderBrush     = BorderC,
                BorderThickness = new Thickness(1),
                Margin          = new Thickness(0, 0, 0, 8),
                ClipToBounds    = true
            };
            var outer = new StackPanel();

            var original   = group.Files.FirstOrDefault(f => f.IsOriginal) ?? group.Files[0];
            var duplicates = group.Files.Where(f => !f.IsOriginal).ToList();

            // ── Collapse/expand state ────────────────────────────────────
            bool expanded = true;

            // Arrow indicator (▼ / ▶)
            var arrow = new TextBlock
            {
                Text = "▼", FontSize = 9,
                Foreground = TextMut,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 9, 0)
            };

            // ── ORIGINAL header row — click to expand/collapse ───────────
            var hdrBorder = new Border
            {
                Padding    = new Thickness(14, 10, 14, 10),
                Background = new SolidColorBrush(Color.FromRgb(246, 248, 250)),
                Cursor     = Cursors.Hand
            };
            var hdrDock = new DockPanel();
            hdrDock.Children.Add(arrow); // docks left

            if (IsImageFile(original.FullPath))
                hdrDock.Children.Add(BuildThumb(original));

            // ORIGINAL badge (right)
            var badge = new Border
            {
                CornerRadius = new CornerRadius(4),
                Background   = AccentLightC,
                Padding      = new Thickness(7, 2, 7, 2),
                Margin       = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var bsp = new StackPanel { Orientation = Orientation.Horizontal };
            bsp.Children.Add(MakeIcon("IconShield", AccentC, 9));
            bsp.Children.Add(new TextBlock { Text = " ORIGINAL", FontSize = 8, FontWeight = FontWeights.Bold, Foreground = AccentC });
            badge.Child = bsp;
            DockPanel.SetDock(badge, Dock.Right);
            hdrDock.Children.Add(badge);

            // Size / date (right)
            var hdrSz = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            DockPanel.SetDock(hdrSz, Dock.Right);
            hdrSz.Children.Add(new TextBlock { Text = original.SizeHuman, FontSize = 11, Foreground = Text2, HorizontalAlignment = HorizontalAlignment.Right });
            hdrSz.Children.Add(new TextBlock { Text = original.Modified,  FontSize = 9,  Foreground = TextMut, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 1, 0, 0) });
            hdrDock.Children.Add(hdrSz);

            // File name + directory (fill)
            var hdrInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            hdrInfo.Children.Add(new TextBlock { Text = original.FileName,  FontSize = 12, FontWeight = FontWeights.Medium, Foreground = Text1, TextTrimming = TextTrimming.CharacterEllipsis });
            hdrInfo.Children.Add(new TextBlock { Text = original.Directory, FontSize = 10, Foreground = TextMut, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
            hdrDock.Children.Add(hdrInfo);

            hdrBorder.Child = hdrDock;
            outer.Children.Add(hdrBorder);

            // ── Duplicates section (collapsible) ─────────────────────────
            var dupSection = new StackPanel();

            // Sub-header "Дубликаты (N)"
            int dc = duplicates.Count;
            string plural = dc == 1 ? "дубликат" : dc < 5 ? "дубликата" : "дубликатов";
            var subHdr = new Border
            {
                Padding = new Thickness(14, 6, 14, 6),
                Background = new SolidColorBrush(Color.FromRgb(255, 249, 240)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(238, 226, 210)),
                BorderThickness = new Thickness(0, 1, 0, 0)
            };
            subHdr.Child = new TextBlock
            {
                Text = $"Дубликаты ({dc} {plural})",
                FontSize = 10, FontWeight = FontWeights.SemiBold,
                Foreground = WarningC
            };
            dupSection.Children.Add(subHdr);

            // One row per duplicate
            foreach (var dup in duplicates)
            {
                var row = new Border
                {
                    Padding         = new Thickness(14, 7, 14, 7),
                    BorderBrush     = new SolidColorBrush(Color.FromRgb(240, 236, 232)),
                    BorderThickness = new Thickness(0, 1, 0, 0)
                };
                var rowDock = new DockPanel();

                // Indent to align with original content
                rowDock.Children.Add(new Border { Width = 26 });

                var cb = new CheckBox
                {
                    Style = (Style)FindResource("RedCheck"),
                    IsChecked = dup.IsSelected,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 10, 0)
                };
                cb.Checked   += (s, ev) => { dup.IsSelected = true;  UpdateCount(); };
                cb.Unchecked += (s, ev) => { dup.IsSelected = false; UpdateCount(); };
                rowDock.Children.Add(cb);

                // File row (thumb + name + size)
                var fileRow = BuildFileRow(dup);
                foreach (UIElement child in fileRow.Children.OfType<UIElement>().ToList())
                    rowDock.Children.Add(child);

                // Need to re-add since we can't share children — rebuild inline
                rowDock.Children.Clear();
                rowDock.Children.Add(new Border { Width = 26 });
                rowDock.Children.Add(cb);
                if (IsImageFile(dup.FullPath))
                    rowDock.Children.Add(BuildThumb(dup));

                var dupSz = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
                DockPanel.SetDock(dupSz, Dock.Right);
                dupSz.Children.Add(new TextBlock { Text = dup.SizeHuman, FontSize = 11, Foreground = Text2, HorizontalAlignment = HorizontalAlignment.Right });
                dupSz.Children.Add(new TextBlock { Text = dup.Modified,  FontSize = 9,  Foreground = TextMut, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 1, 0, 0) });
                rowDock.Children.Add(dupSz);

                var dupInfo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                dupInfo.Children.Add(new TextBlock { Text = dup.FileName,  FontSize = 12, FontWeight = FontWeights.Medium, Foreground = Text1, TextTrimming = TextTrimming.CharacterEllipsis });
                dupInfo.Children.Add(new TextBlock { Text = dup.Directory, FontSize = 10, Foreground = TextMut, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
                rowDock.Children.Add(dupInfo);

                row.Child = rowDock;
                dupSection.Children.Add(row);
            }

            outer.Children.Add(dupSection);
            card.Child = outer;

            // ── Toggle expand/collapse ────────────────────────────────────
            hdrBorder.MouseLeftButtonUp += (s, ev) =>
            {
                expanded = !expanded;
                dupSection.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
                arrow.Text = expanded ? "▼" : "▶";
            };

            return card;
        }

        // ─── Junk ────────────────────────────────────────────────────────

        private void ShowJunk(List<JunkCategory> cats)
        {
            EmptyState.Visibility   = Visibility.Collapsed;
            SuccessState.Visibility = Visibility.Collapsed;

            int tc = cats.Sum(c => c.FileCount);
            if (tc == 0)
            {
                ShowSuccessScreen("Мусор не найден", "Система чистая");
                return;
            }

            ShowSummary(
                ("Мусора найдено", ScannerService.FormatSize(cats.Sum(c => c.TotalSize)), true),
                ("Файлов",        tc.ToString("N0"),                                     true));

            var items = new List<UIElement>();
            foreach (var cat in cats)
            {
                var border = new Border
                {
                    CornerRadius    = new CornerRadius(8),
                    Background      = CardBg,
                    BorderBrush     = BorderC,
                    BorderThickness = new Thickness(1),
                    Padding         = new Thickness(14, 11, 14, 11),
                    Margin          = new Thickness(0, 0, 0, 6),
                    Cursor          = System.Windows.Input.Cursors.Hand
                };
                var dock = new DockPanel();

                var cb = new CheckBox
                {
                    Style = (Style)FindResource("GreenCheck"),
                    IsChecked = cat.IsSelected,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0)
                };
                cb.Checked   += (s, ev) => { cat.IsSelected = true;  UpdateCount(); };
                cb.Unchecked += (s, ev) => { cat.IsSelected = false; UpdateCount(); };
                dock.Children.Add(cb);

                var badge = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Background   = DangerLightC,
                    Padding      = new Thickness(9, 3, 9, 3),
                    VerticalAlignment = VerticalAlignment.Center
                };
                badge.Child = new TextBlock
                    { Text = cat.TotalSizeHuman, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = DangerC };
                DockPanel.SetDock(badge, Dock.Right);
                dock.Children.Add(badge);

                var icon = MakeIcon(cat.IconKey, TextDim, 18);
                icon.Margin = new Thickness(0, 0, 11, 0);
                dock.Children.Add(icon);

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock
                    { Text = cat.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Text1 });
                info.Children.Add(new TextBlock
                    { Text = $"{cat.FileCount:N0} файлов", FontSize = 11, Foreground = TextMut, Margin = new Thickness(0, 2, 0, 0) });
                dock.Children.Add(info);

                border.Child = dock;
                border.MouseLeftButtonUp += (s, ev) => { cat.IsSelected = !cat.IsSelected; cb.IsChecked = cat.IsSelected; };
                items.Add(border);
            }

            ResultsList.ItemsSource = items;
            ShowResultsAnimated();
            TxtDeleteLabel.Text  = "Очистить выбранные";
            BtnDelete.Visibility = Visibility.Visible;
            UpdateCount();
        }

        // ─── Large Files ─────────────────────────────────────────────────

        private void ShowLarge(List<FileItem> files)
        {
            EmptyState.Visibility   = Visibility.Collapsed;
            SuccessState.Visibility = Visibility.Collapsed;

            if (files.Count == 0)
            {
                ShowSuccessScreen("Крупных файлов не найдено", "");
                return;
            }

            ShowSummary(
                ("Найдено",       files.Count.ToString(),                      false),
                ("Общий размер",  ScannerService.FormatSize(files.Sum(f => f.Size)), true));

            var items = new List<UIElement>();
            foreach (var file in files)
            {
                var border = new Border
                {
                    CornerRadius    = new CornerRadius(8),
                    Background      = CardBg,
                    BorderBrush     = BorderC,
                    BorderThickness = new Thickness(1),
                    Padding         = new Thickness(14, 9, 14, 9),
                    Margin          = new Thickness(0, 0, 0, 5)
                };
                var dock = new DockPanel();

                var cb = new CheckBox
                {
                    Style = (Style)FindResource("RedCheck"),
                    IsChecked = file.IsSelected,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0)
                };
                cb.Checked   += (s, ev) => { file.IsSelected = true;  UpdateCount(); };
                cb.Unchecked += (s, ev) => { file.IsSelected = false; UpdateCount(); };
                dock.Children.Add(cb);

                var badge = new Border
                {
                    CornerRadius = new CornerRadius(6),
                    Background   = WarningLightC,
                    Padding      = new Thickness(9, 3, 9, 3),
                    VerticalAlignment = VerticalAlignment.Center
                };
                badge.Child = new TextBlock
                    { Text = file.SizeHuman, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = WarningC };
                DockPanel.SetDock(badge, Dock.Right);
                dock.Children.Add(badge);

                var icon = MakeIcon("IconFile", TextDim, 16);
                icon.Margin = new Thickness(0, 0, 11, 0);
                dock.Children.Add(icon);

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                info.Children.Add(new TextBlock
                    { Text = file.FileName, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = Text1, TextTrimming = TextTrimming.CharacterEllipsis });
                info.Children.Add(new TextBlock
                    { Text = $"{file.Directory}  ·  {file.Extension}", FontSize = 10, Foreground = TextMut, Margin = new Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis });
                dock.Children.Add(info);

                border.Child = dock;
                items.Add(border);
            }

            ResultsList.ItemsSource = items;
            ShowResultsAnimated();
            TxtDeleteLabel.Text  = "Удалить выбранные";
            BtnDelete.Visibility = Visibility.Visible;
            UpdateCount();
        }

        // ─── Disk Analysis ───────────────────────────────────────────────────

        private void ShowDiskAnalysis(List<FolderSizeItem> folders)
        {
            EmptyState.Visibility   = Visibility.Collapsed;
            SuccessState.Visibility = Visibility.Collapsed;

            if (folders.Count == 0)
            {
                ShowSuccessScreen("Нет данных", "");
                return;
            }

            ShowSummary(
                ("Папок",       folders.Count.ToString(),                               false),
                ("Общий объём", ScannerService.FormatSize(folders.Sum(f => f.Size)),    true));

            var items = new List<UIElement>();
            foreach (var folder in folders)
            {
                var border = new Border
                {
                    CornerRadius    = new CornerRadius(8),
                    Background      = CardBg,
                    BorderBrush     = BorderC,
                    BorderThickness = new Thickness(1),
                    Padding         = new Thickness(14, 11, 14, 11),
                    Margin          = new Thickness(0, 0, 0, 5)
                };

                var stack = new StackPanel();

                // Row 1: icon + name + size
                var top = new DockPanel { Margin = new Thickness(0, 0, 0, 7) };
                var szLabel = new TextBlock { Text = folder.SizeHuman, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = AccentC, VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(szLabel, Dock.Right);
                top.Children.Add(szLabel);
                top.Children.Add(MakeIcon("IconFolder", TextDim, 15));
                var nameBlock = new TextBlock { Text = "  " + folder.Name, FontSize = 13, FontWeight = FontWeights.Medium, Foreground = Text1, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
                top.Children.Add(nameBlock);
                stack.Children.Add(top);

                // Row 2: bar
                var barBg = new Border { Height = 5, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromRgb(228, 233, 240)), ClipToBounds = true };
                var barFill = new Border { CornerRadius = new CornerRadius(3), Background = AccentC, HorizontalAlignment = HorizontalAlignment.Left };
                var barGrid = new Grid();
                barGrid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromRgb(228, 233, 240)) });
                barGrid.Children.Add(barFill);
                barBg.Child = barGrid;
                double rel = folder.RelativeSize;
                barGrid.Loaded += (s, ev) => barFill.Width = barGrid.ActualWidth * rel;
                stack.Children.Add(barBg);

                // Row 3: path + file count
                var sub = new DockPanel { Margin = new Thickness(0, 5, 0, 0) };
                var fcLabel = new TextBlock { Text = $"{folder.FileCount:N0} файлов", FontSize = 10, Foreground = TextMut };
                DockPanel.SetDock(fcLabel, Dock.Right);
                sub.Children.Add(fcLabel);
                sub.Children.Add(new TextBlock { Text = folder.FullPath, FontSize = 10, Foreground = TextMut, TextTrimming = TextTrimming.CharacterEllipsis });
                stack.Children.Add(sub);

                border.Child = stack;
                items.Add(border);
            }

            ResultsList.ItemsSource = items;
            ShowResultsAnimated();
            BtnDelete.Visibility = Visibility.Collapsed;
            UpdateCount();
        }

        // ─── Empty Folders ───────────────────────────────────────────────────

        private void ShowEmptyFolders(List<EmptyFolderItem> folders)
        {
            EmptyState.Visibility   = Visibility.Collapsed;
            SuccessState.Visibility = Visibility.Collapsed;

            if (folders.Count == 0)
            {
                ShowSuccessScreen("Пустых папок нет", "Всё в порядке");
                return;
            }

            ShowSummary(("Найдено пустых папок", folders.Count.ToString("N0"), true));

            var items = new List<UIElement>();
            foreach (var folder in folders)
            {
                var border = new Border
                {
                    CornerRadius    = new CornerRadius(8),
                    Background      = CardBg,
                    BorderBrush     = BorderC,
                    BorderThickness = new Thickness(1),
                    Padding         = new Thickness(14, 9, 14, 9),
                    Margin          = new Thickness(0, 0, 0, 4)
                };
                var dock = new DockPanel();

                var cb = new CheckBox
                {
                    Style = (Style)FindResource("RedCheck"),
                    IsChecked = folder.IsSelected,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0)
                };
                cb.Checked   += (s, ev) => { folder.IsSelected = true;  UpdateCount(); };
                cb.Unchecked += (s, ev) => { folder.IsSelected = false; UpdateCount(); };
                dock.Children.Add(cb);

                dock.Children.Add(MakeIcon("IconFolder", TextDim, 15));

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0) };
                info.Children.Add(new TextBlock { Text = folder.Name, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = Text1, TextTrimming = TextTrimming.CharacterEllipsis });
                info.Children.Add(new TextBlock { Text = folder.Parent, FontSize = 10, Foreground = TextMut, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
                dock.Children.Add(info);

                border.Child = dock;
                items.Add(border);
            }

            ResultsList.ItemsSource = items;
            ShowResultsAnimated();
            TxtDeleteLabel.Text  = "Удалить пустые папки";
            BtnDelete.Visibility = Visibility.Visible;
            UpdateCount();
        }

        // ─── Startup Manager ─────────────────────────────────────────────────

        private void ShowStartup(List<StartupItem> startups)
        {
            EmptyState.Visibility   = Visibility.Collapsed;
            SuccessState.Visibility = Visibility.Collapsed;

            if (startups.Count == 0)
            {
                ShowSuccessScreen("Записей автозапуска нет", "");
                return;
            }

            int disabled = startups.Count(s => !s.IsEnabled);
            ShowSummary(
                ("Записей",   startups.Count.ToString(), false),
                ("Отключено", disabled.ToString(),        disabled > 0));

            var items = new List<UIElement>();
            foreach (var entry in startups)
            {
                var border = new Border
                {
                    CornerRadius    = new CornerRadius(8),
                    Background      = CardBg,
                    BorderBrush     = BorderC,
                    BorderThickness = new Thickness(1),
                    Padding         = new Thickness(14, 9, 14, 9),
                    Margin          = new Thickness(0, 0, 0, 4),
                    Opacity         = entry.IsEnabled ? 1.0 : 0.55
                };
                var dock = new DockPanel();

                var cb = new CheckBox
                {
                    Style = (Style)FindResource("RedCheck"),
                    IsChecked = entry.IsSelected,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0)
                };
                cb.Checked   += (s, ev) => { entry.IsSelected = true;  UpdateCount(); };
                cb.Unchecked += (s, ev) => { entry.IsSelected = false; UpdateCount(); };
                dock.Children.Add(cb);

                // Source badge (right-docked — must be added before fill child)
                var src = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Background   = AccentLightC,
                    Padding      = new Thickness(6, 2, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center
                };
                src.Child = new TextBlock { Text = entry.Source, FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = AccentC };
                DockPanel.SetDock(src, Dock.Right);
                dock.Children.Add(src);

                dock.Children.Add(MakeIcon("IconStartup", TextDim, 15));

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0) };
                info.Children.Add(new TextBlock { Text = entry.Name, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = Text1, TextTrimming = TextTrimming.CharacterEllipsis });
                info.Children.Add(new TextBlock { Text = entry.Command, FontSize = 10, Foreground = TextMut, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
                dock.Children.Add(info);

                border.Child = dock;
                items.Add(border);
            }

            ResultsList.ItemsSource = items;
            ShowResultsAnimated();
            TxtDeleteLabel.Text  = "Удалить из автозапуска";
            BtnDelete.Visibility = Visibility.Visible;
            UpdateCount();
        }

        // ─── Broken Shortcuts ────────────────────────────────────────────────

        private void ShowShortcuts(List<ShortcutItem> shortcuts)
        {
            EmptyState.Visibility   = Visibility.Collapsed;
            SuccessState.Visibility = Visibility.Collapsed;

            if (shortcuts.Count == 0)
            {
                ShowSuccessScreen("Сломанных ярлыков нет", "Все ярлыки указывают на существующие файлы");
                return;
            }

            ShowSummary(("Сломанных ярлыков", shortcuts.Count.ToString("N0"), true));

            var items = new List<UIElement>();
            foreach (var sc in shortcuts)
            {
                var border = new Border
                {
                    CornerRadius    = new CornerRadius(8),
                    Background      = CardBg,
                    BorderBrush     = BorderC,
                    BorderThickness = new Thickness(1),
                    Padding         = new Thickness(14, 9, 14, 9),
                    Margin          = new Thickness(0, 0, 0, 4)
                };
                var dock = new DockPanel();

                var cb = new CheckBox
                {
                    Style = (Style)FindResource("RedCheck"),
                    IsChecked = sc.IsSelected,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0)
                };
                cb.Checked   += (s, ev) => { sc.IsSelected = true;  UpdateCount(); };
                cb.Unchecked += (s, ev) => { sc.IsSelected = false; UpdateCount(); };
                dock.Children.Add(cb);

                // Location badge (right-docked — must come before fill child)
                var locBadge = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Background   = new SolidColorBrush(Color.FromRgb(234, 238, 242)),
                    Padding      = new Thickness(6, 2, 6, 2),
                    VerticalAlignment = VerticalAlignment.Center
                };
                locBadge.Child = new TextBlock { Text = sc.Location, FontSize = 9, FontWeight = FontWeights.SemiBold, Foreground = TextMut };
                DockPanel.SetDock(locBadge, Dock.Right);
                dock.Children.Add(locBadge);

                dock.Children.Add(MakeIcon("IconLink", TextDim, 15));

                var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(9, 0, 0, 0) };
                info.Children.Add(new TextBlock { Text = sc.ShortcutName, FontSize = 12, FontWeight = FontWeights.Medium, Foreground = Text1, TextTrimming = TextTrimming.CharacterEllipsis });
                info.Children.Add(new TextBlock { Text = sc.TargetPath, FontSize = 10, Foreground = DangerC, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
                dock.Children.Add(info);

                border.Child = dock;
                items.Add(border);
            }

            ResultsList.ItemsSource = items;
            ShowResultsAnimated();
            TxtDeleteLabel.Text  = "Удалить ярлыки";
            BtnDelete.Visibility = Visibility.Visible;
            UpdateCount();
        }

        // ─── Delete ──────────────────────────────────────────────────────

        private void OnDelete(object sender, RoutedEventArgs e)
        {
            // Empty folders: delete directories
            if (_currentTab == "empty")
            {
                var toDelete = _emptyResults?.Where(f => f.IsSelected).Select(f => f.FullPath).ToList() ?? new();
                if (toDelete.Count == 0) return;
                if (MessageBox.Show($"Удалить {toDelete.Count:N0} пустых папок?",
                        "CleanSweep", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                var (delFolders, errFolders) = ScannerService.DeleteFolders(toDelete);
                _emptyResults = _emptyResults?.Where(f => !f.IsSelected).ToList();
                if (_emptyResults?.Count == 0) { ShowEmpty(); _emptyResults = null; }
                else ShowEmptyFolders(_emptyResults!);
                MessageBox.Show($"Удалено: {delFolders}  ·  Ошибок: {errFolders}", "CleanSweep", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Startup: delete registry entries
            if (_currentTab == "startup")
            {
                var toDelete = _startupResults?.Where(i => i.IsSelected).ToList() ?? new();
                if (toDelete.Count == 0) return;
                if (MessageBox.Show($"Удалить {toDelete.Count:N0} записей из автозапуска?",
                        "CleanSweep", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                foreach (var item in toDelete) ScannerService.DeleteStartupItem(item);
                _startupResults = _startupResults?.Where(i => !i.IsSelected).ToList();
                if (_startupResults?.Count == 0) { ShowEmpty(); _startupResults = null; }
                else ShowStartup(_startupResults!);
                return;
            }

            // Broken shortcuts: delete the .lnk files themselves
            if (_currentTab == "shortcuts")
            {
                var toDelete = _shortcutsResults?.Where(i => i.IsSelected).Select(i => i.ShortcutPath).ToList() ?? new();
                if (toDelete.Count == 0) return;
                if (MessageBox.Show($"Удалить {toDelete.Count:N0} сломанных ярлыков?",
                        "CleanSweep", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                var (scDel, scErr) = ScannerService.DeleteFiles(toDelete);
                _shortcutsResults = _shortcutsResults?.Where(i => !i.IsSelected).ToList();
                if (_shortcutsResults?.Count == 0) { ShowEmpty(); _shortcutsResults = null; }
                else ShowShortcuts(_shortcutsResults!);
                MessageBox.Show($"Удалено: {scDel}  ·  Ошибок: {scErr}", "CleanSweep", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var paths = new List<string>();
            switch (_currentTab)
            {
                case "dup_images": paths = _dupImageResults?.SelectMany(g => g.Files).Where(f => f.IsSelected).Select(f => f.FullPath).ToList() ?? new(); break;
                case "dup_files":  paths = _dupFileResults ?.SelectMany(g => g.Files).Where(f => f.IsSelected).Select(f => f.FullPath).ToList() ?? new(); break;
                case "junk":       paths = _junkResults    ?.Where(c => c.IsSelected).SelectMany(c => c.FilePaths).ToList() ?? new(); break;
                case "large":      paths = _largeResults   ?.Where(f => f.IsSelected).Select(f => f.FullPath).ToList() ?? new(); break;
            }
            if (paths.Count == 0) return;
            if (MessageBox.Show($"Удалить {paths.Count:N0} файлов?\n\nЭто действие необратимо.",
                    "CleanSweep", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            // Calculate freed space BEFORE deletion
            long freed = paths.Sum(p => { try { return new FileInfo(p).Length; } catch { return 0L; } });
            var (del, err) = ScannerService.DeleteFiles(paths);

            ShowSuccessScreen("Готово! Всё чисто",
                $"Удалено: {del}  ·  Освобождено: {ScannerService.FormatSize(freed)}  ·  Ошибок: {err}");

            switch (_currentTab)
            {
                case "dup_images": _dupImageResults  = null; break;
                case "dup_files":  _dupFileResults   = null; break;
                case "junk":       _junkResults      = null; break;
                case "large":      _largeResults     = null; break;
            }
        }

        // ─── Helpers ─────────────────────────────────────────────────────

        private void UpdateCount()
        {
            if (_currentTab == "disk") return; // analysis only, no delete

            int c = _currentTab switch
            {
                "dup_images" => _dupImageResults ?.SelectMany(g => g.Files).Count(f => f.IsSelected) ?? 0,
                "dup_files"  => _dupFileResults  ?.SelectMany(g => g.Files).Count(f => f.IsSelected) ?? 0,
                "junk"       => _junkResults     ?.Count(c2 => c2.IsSelected) ?? 0,
                "large"      => _largeResults    ?.Count(f => f.IsSelected) ?? 0,
                "empty"      => _emptyResults    ?.Count(f => f.IsSelected) ?? 0,
                "startup"    => _startupResults  ?.Count(i => i.IsSelected) ?? 0,
                "shortcuts"  => _shortcutsResults?.Count(i => i.IsSelected) ?? 0,
                _            => 0,
            };

            TxtDeleteLabel.Text = _currentTab switch
            {
                "junk"      => $"Очистить выбранные ({c})",
                "startup"   => $"Удалить из автозапуска ({c})",
                "shortcuts" => $"Удалить ярлыки ({c})",
                "empty"     => $"Удалить папки ({c})",
                _           => $"Удалить выбранные ({c})",
            };
        }

        private void ShowSummary(params (string label, string value, bool danger)[] cards)
        {
            SummaryContent.Children.Clear();
            foreach (var (label, value, danger) in cards)
            {
                var b = new Border
                {
                    Padding         = new Thickness(14, 8, 14, 8),
                    CornerRadius    = new CornerRadius(6),
                    Background      = CardBg,
                    BorderBrush     = BorderC,
                    BorderThickness = new Thickness(1),
                    Margin          = new Thickness(0, 0, 10, 0)
                };
                var s = new StackPanel();
                s.Children.Add(new TextBlock { Text = value, FontSize = 20, FontWeight = FontWeights.ExtraBold, Foreground = danger ? DangerC : Text1 });
                s.Children.Add(new TextBlock { Text = label, FontSize = 10, Foreground = TextMut, Margin = new Thickness(0, 2, 0, 0) });
                b.Child = s;
                SummaryContent.Children.Add(b);
            }
        }

        // ─── Folder Selection ────────────────────────────────────────────

        private void OnAddFolder(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title       = "Выберите папку для сканирования",
                Multiselect = true
            };
            if (dlg.ShowDialog() != true) return;
            _scanFolders ??= new List<string>();
            foreach (var f in dlg.FolderNames)
                if (!_scanFolders.Contains(f, StringComparer.OrdinalIgnoreCase))
                    _scanFolders.Add(f);
            RefreshFolderChips();
        }

        private void RefreshFolderChips()
        {
            FolderChips.Children.Clear();
            if (_scanFolders is not { Count: > 0 })
            {
                FolderChips.Children.Add(MakeFolderChip("Весь компьютер", null));
                return;
            }
            foreach (var folder in _scanFolders)
            {
                var name = Path.GetFileName(folder.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(name)) name = folder; // root like "C:\"
                FolderChips.Children.Add(MakeFolderChip(name, folder));
            }
        }

        private Border MakeFolderChip(string label, string? removePath)
        {
            bool isDefault = removePath == null;
            var chip = new Border
            {
                CornerRadius    = new CornerRadius(100),
                Background      = isDefault
                    ? new SolidColorBrush(Color.FromRgb(234, 238, 242))
                    : AccentLightC,
                BorderBrush     = isDefault ? BorderC : AccentC,
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(8, 3, 8, 3),
                Margin          = new Thickness(0, 0, 5, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var sp = new StackPanel { Orientation = Orientation.Horizontal };

            sp.Children.Add(MakeIcon("IconFolder",
                isDefault ? TextMut : AccentC, 10));

            sp.Children.Add(new TextBlock
            {
                Text              = label,
                FontSize          = 11,
                Foreground        = isDefault ? TextMut : AccentC,
                VerticalAlignment = VerticalAlignment.Center,
                Margin            = new Thickness(4, 0, 0, 0),
                MaxWidth          = 140,
                TextTrimming      = TextTrimming.CharacterEllipsis
            });

            if (!isDefault)
            {
                var rm = new TextBlock
                {
                    Text              = "×",
                    FontSize          = 14,
                    Foreground        = AccentC,
                    Margin            = new Thickness(4, 0, -2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Cursor            = Cursors.Hand
                };
                var cap = removePath;
                rm.MouseLeftButtonUp += (s, ev) =>
                {
                    _scanFolders?.Remove(cap!);
                    if (_scanFolders?.Count == 0) _scanFolders = null;
                    RefreshFolderChips();
                };
                sp.Children.Add(rm);
            }

            chip.Child = sp;
            return chip;
        }

        // ─── Auto-Update ─────────────────────────────────────────────────

        private async Task CheckForUpdatesAsync()
        {
            if (string.IsNullOrEmpty(GitHubRepo)) return;
            try
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.UserAgent.ParseAdd("CleanSweep/" + AppVersion);
                http.Timeout = TimeSpan.FromSeconds(6);

                var json = await http.GetStringAsync(
                    $"https://api.github.com/repos/{GitHubRepo}/releases/latest");

                using var doc = JsonDocument.Parse(json);
                var root    = doc.RootElement;
                var tag     = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
                var htmlUrl = root.GetProperty("html_url").GetString() ?? "";

                // Try to find a direct .exe asset
                string downloadUrl = htmlUrl;
                if (root.TryGetProperty("assets", out var assets))
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        var name = asset.GetProperty("name").GetString() ?? "";
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            downloadUrl = asset.GetProperty("browser_download_url")
                                               .GetString() ?? htmlUrl;
                            break;
                        }
                    }
                }

                if (IsNewerVersion(tag, AppVersion))
                    Dispatcher.BeginInvoke(() => ShowUpdateBanner(tag, downloadUrl));
            }
            catch { /* silently ignore — no internet, rate limit, wrong repo name, etc. */ }
        }

        private static bool IsNewerVersion(string remote, string current)
            => Version.TryParse(remote, out var r)
            && Version.TryParse(current, out var c)
            && r > c;

        private void ShowUpdateBanner(string version, string url)
        {
            _releaseUrl               = url;
            UpdateVersionText.Text    = $"v{version} готова к загрузке";
            UpdateBanner.Visibility   = Visibility.Visible;
            FadeIn(UpdateBanner);
        }

        private void OnUpdateClick(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_releaseUrl)) return;
            Process.Start(new ProcessStartInfo(_releaseUrl) { UseShellExecute = true });
        }

        // ─── Disk info ───────────────────────────────────────────────────

        private void LoadDiskInfo()
        {
            try
            {
                var drives = DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed);
                var items  = new List<UIElement>();
                foreach (var d in drives)
                {
                    var pct   = (double)(d.TotalSize - d.AvailableFreeSpace) / d.TotalSize * 100;
                    var color = pct > 90 ? DangerC : pct > 70 ? WarningC : SideAccent;

                    var panel  = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
                    var header = new DockPanel();
                    header.Children.Add(new TextBlock { Text = d.Name, FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = SideText });
                    var ft = new TextBlock
                    {
                        Text = ScannerService.FormatSize(d.AvailableFreeSpace) + " свободно",
                        FontSize = 9, Foreground = SideMuted,
                        HorizontalAlignment = HorizontalAlignment.Right
                    };
                    DockPanel.SetDock(ft, Dock.Right);
                    header.Children.Add(ft);
                    panel.Children.Add(header);

                    var barBg  = new Border { Height = 3, CornerRadius = new CornerRadius(2), Background = SideBar, Margin = new Thickness(0, 4, 0, 0), ClipToBounds = true };
                    var grid   = new Grid();
                    var barFill= new Border { CornerRadius = new CornerRadius(2), Background = color, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
                    grid.Children.Add(barFill);
                    barBg.Child = grid;
                    panel.Children.Add(barBg);
                    panel.Loaded += (s, ev) => barFill.Width = grid.ActualWidth * pct / 100;
                    items.Add(panel);
                }
                DiskList.ItemsSource = items;
            }
            catch { }
        }
    }
}
