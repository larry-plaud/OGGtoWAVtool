using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WpfColor = System.Windows.Media.Color;

namespace OggConverter
{
    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<FolderEntry> _folders = new();
        private readonly ObservableCollection<LogEntry>    _log     = new();
        private CancellationTokenSource? _cts;
        private string _outputFolder    = "";
        private double _currentProgress = 0;

        public MainWindow()
        {
            InitializeComponent();
            FolderListBox.ItemsSource = _folders;
            LogListBox.ItemsSource    = _log;

            FolderListBox.SelectionChanged += (_, _) =>
                RemoveFolderBtn.IsEnabled = FolderListBox.SelectedItem != null;

            Log("欢迎使用 OGG → WAV 批量转换工具。");
            Log("支持 OGG Vorbis（无需额外工具）和 OGG Opus（需要 ffmpeg.exe）。");

            // 检测 FFmpeg
            var ff = AudioConverter.FindFfmpeg();
            if (ff != null)
                Log($"FFmpeg 已就绪: {ff}", LogLevel.Success);
            else
                Log("未找到 ffmpeg.exe — Opus 文件将无法转换（Vorbis 文件不受影响）", LogLevel.Warning);
        }

        private void ProgressTrack_SizeChanged(object sender, SizeChangedEventArgs e)
            => SetProgress(_currentProgress);

        private void Log(string msg, LogLevel level = LogLevel.Info)
        {
            Dispatcher.Invoke(() =>
            {
                _log.Add(new LogEntry { Message = msg, Level = level });
                if (_log.Count > 0) LogListBox.ScrollIntoView(_log[^1]);
            });
        }

        private void RefreshFolderStats()
        {
            int total = _folders.Sum(f =>
                int.TryParse(f.OggCount.Split(' ')[0], out int n) ? n : 0);
            FolderCountText.Text      = $"{_folders.Count} 个文件夹";
            FileCountText.Text        = total > 0 ? $"· 共 {total} 个 OGG" : "";
            EmptyHint.Visibility      = _folders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ClearFoldersBtn.IsEnabled = _folders.Count > 0;
        }

        private void SetProgress(double v)
        {
            _currentProgress = v;
            double w = ProgressTrack.ActualWidth;
            if (w > 0) ProgressFill.Width = Math.Max(0, v * w);
        }

        private void SetBusy(bool busy)
        {
            ConvertBtn.IsEnabled      = !busy;
            AddFolderBtn.IsEnabled    = !busy;
            RemoveFolderBtn.IsEnabled = !busy && FolderListBox.SelectedItem != null;
            ClearFoldersBtn.IsEnabled = !busy && _folders.Count > 0;
            BrowseOutputBtn.IsEnabled = !busy;
            CancelBtn.IsEnabled       =  busy;
        }

        private void AddFolder_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description            = "选择包含 OGG 文件的文件夹",
                UseDescriptionForTitle = true,
                ShowNewFolderButton    = false
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            string path = dlg.SelectedPath;
            if (_folders.Any(f => f.FullPath.Equals(path, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("该文件夹已在列表中。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var entry = FolderEntry.From(path);
            _folders.Add(entry);
            RefreshFolderStats();
            Log($"已添加: {path}（{entry.OggCount}）");
        }

        private void RemoveFolder_Click(object sender, RoutedEventArgs e)
        {
            if (FolderListBox.SelectedItem is FolderEntry fe)
            {
                _folders.Remove(fe);
                RefreshFolderStats();
                Log($"已移除: {fe.FullPath}", LogLevel.Warning);
            }
        }

        private void ClearFolders_Click(object sender, RoutedEventArgs e)
        {
            _folders.Clear();
            RefreshFolderStats();
            Log("已清空文件夹列表。", LogLevel.Warning);
        }

        private void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            using var dlg = new System.Windows.Forms.FolderBrowserDialog
            {
                Description            = "选择 WAV 输出文件夹",
                UseDescriptionForTitle = true,
                ShowNewFolderButton    = true
            };
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
            _outputFolder             = dlg.SelectedPath;
            OutputPathText.Text       = _outputFolder;
            OutputPathText.Foreground = new SolidColorBrush(WpfColor.FromRgb(240, 238, 248));
            Log($"输出目录: {_outputFolder}");
        }

        private void ClearLog_Click(object sender, RoutedEventArgs e) => _log.Clear();

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            Log("正在取消…", LogLevel.Warning);
        }

        private async void Convert_Click(object sender, RoutedEventArgs e)
        {
            if (_folders.Count == 0)
            {
                MessageBox.Show("请先添加至少一个源文件夹。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var files = new List<(string src, string rel, string root)>();
            foreach (var f in _folders)
            {
                try
                {
                    foreach (var p in Directory.GetFiles(f.FullPath, "*.ogg",
                                                         SearchOption.AllDirectories))
                        files.Add((p, Path.GetRelativePath(f.FullPath, p), f.FullPath));
                }
                catch (Exception ex)
                {
                    Log($"扫描失败: {f.FullPath} — {ex.Message}", LogLevel.Error);
                }
            }

            if (files.Count == 0)
            {
                MessageBox.Show("未找到 OGG 文件。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 预检：如果有 Opus 文件但没有 FFmpeg，提前警告
            bool hasOpus = files.Any(f => AudioConverter.IsOpus(f.src));
            if (hasOpus && AudioConverter.FindFfmpeg() == null)
            {
                var r = MessageBox.Show(
                    "检测到 OGG Opus 文件，但未找到 ffmpeg.exe。\n\n" +
                    "Opus 文件将会失败，Vorbis 文件仍会正常转换。\n\n" +
                    "是否继续？\n\n" +
                    "（如需支持 Opus，请将 ffmpeg.exe 放到程序同目录）",
                    "缺少 FFmpeg", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r == MessageBoxResult.No) return;
            }

            bool   overwrite  = OverwriteCheck.IsChecked    == true;
            bool   keepStruct = KeepStructureCheck.IsChecked == true;
            string outBase    = _outputFolder;

            SetBusy(true);
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            Log($"开始转换 {files.Count} 个文件 OGG → WAV …");

            int done = 0, skipped = 0, errors = 0;

            try
            {
                for (int i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    var (src, rel, root) = files[i];
                    string destRel = Path.ChangeExtension(rel, ".wav");
                    string destDir = string.IsNullOrEmpty(outBase)
                        ? (Path.GetDirectoryName(src) ?? root)
                        : keepStruct
                            ? Path.Combine(outBase, Path.GetDirectoryName(destRel) ?? "")
                            : outBase;
                    string dest = Path.Combine(destDir,
                        Path.GetFileNameWithoutExtension(src) + ".wav");

                    if (!overwrite && File.Exists(dest))
                    {
                        skipped++;
                        Log($"跳过（已存在）: {Path.GetFileName(dest)}", LogLevel.Warning);
                    }
                    else
                    {
                        StatusText.Text = $"转换中: {Path.GetFileName(src)}";
                        int idx = i;
                        var fp = new Progress<double>(p => Dispatcher.Invoke(() =>
                        {
                            ProgressText.Text = $"{idx + 1} / {files.Count}";
                            SetProgress((idx + p) / files.Count);
                        }));
                        try
                        {
                            await AudioConverter.ConvertAsync(src, dest, 0, ct, fp);
                            Log($"完成: {Path.GetFileName(src)}", LogLevel.Success);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            errors++;
                            Log($"失败: {Path.GetFileName(src)} — {ex.Message}", LogLevel.Error);
                        }
                    }

                    done++;
                    SetProgress((double)done / files.Count);
                    ProgressText.Text = $"{done} / {files.Count}";
                }

                string sum = $"完成！成功 {done - skipped - errors}";
                if (skipped > 0) sum += $"，跳过 {skipped}";
                if (errors  > 0) sum += $"，失败 {errors}";
                StatusText.Text = sum;
                SetProgress(1.0);
                Log(sum, errors > 0 ? LogLevel.Warning : LogLevel.Success);
            }
            catch (OperationCanceledException)
            {
                StatusText.Text   = "已取消";
                ProgressText.Text = "";
                SetProgress(0);
                Log("转换已取消。", LogLevel.Warning);
            }
            finally
            {
                SetBusy(false);
                _cts?.Dispose();
                _cts = null;
            }
        }
    }
}
