using System;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;

namespace DownloadingImagesFinalWork
{
    public class DownloadItem {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string url { get; set; } = string.Empty;
        public string SavePath { get; set; } = string.Empty;
        public string FileName => System.IO.Path.GetFileName(url);
    }


    public partial class MainWindow : Window
    {
        public ObservableCollection<DownloadItem> ActiveDownloads { get; set; } = new ObservableCollection<DownloadItem>();
        public ObservableCollection<DownloadItem> SucceededDownloads { get; set; } = new ObservableCollection<DownloadItem>();
        public ObservableCollection<DownloadItem> FailedDownloads { get; set; } = new ObservableCollection<DownloadItem>();

        private readonly object _activeLock = new();
        private readonly object _successLock = new();
        private readonly object _failedLock = new();

        private static readonly HttpClient _httpClient = new HttpClient();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _ctsDictionary = new();
        private readonly ConcurrentDictionary<string, ManualResetEventSlim> _pauseDictionary = new();

        public MainWindow()
        {
            InitializeComponent();

            BindingOperations.EnableCollectionSynchronization(ActiveDownloads, _activeLock);
            BindingOperations.EnableCollectionSynchronization(SucceededDownloads, _successLock);
            BindingOperations.EnableCollectionSynchronization(FailedDownloads, _failedLock);

            CurrentDownloads.ItemsSource = ActiveDownloads;
            SuccessfulDownloads.ItemsSource = SucceededDownloads;
            UnsucceedDownloads.ItemsSource = FailedDownloads;
        }

        private void StartDownload(object sender, RoutedEventArgs e)
        {
            string url = downloadPath.Text.Trim();
            string savePath1 = savePath.Text.Trim();

            string fileName = System.IO.Path.GetFileName(url);
            if (string.IsNullOrEmpty(fileName)) fileName = "downloaded_file.txt"; 
            string fullSavePath = System.IO.Path.Combine(savePath1, fileName);

            var item = new DownloadItem { url = url, SavePath = fullSavePath };
            ActiveDownloads.Add(item);

            var cts = new CancellationTokenSource();
            _ctsDictionary.TryAdd(item.Id, cts);

            var mres = new ManualResetEventSlim(true);
            _pauseDictionary.TryAdd(item.Id, mres);

            downloadPath.Clear();

            _ = Task.Run(async () =>
            {
                try
                {
                    if (!Directory.Exists(savePath1))
                    {
                        Directory.CreateDirectory(savePath1);
                    }

                    using (HttpResponseMessage response = await _httpClient.GetAsync(item.url, HttpCompletionOption.ResponseHeadersRead, cts.Token))
                    {
                        response.EnsureSuccessStatusCode();

                        using (Stream streamToReadFrom = await response.Content.ReadAsStreamAsync(cts.Token))
                        using (Stream streamToWriteTo = File.Open(item.SavePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                        {
                            byte[] buffer = new byte[8192]; 
                            int bytesRead;

                            while ((bytesRead = await streamToReadFrom.ReadAsync(buffer, 0, buffer.Length, cts.Token)) > 0)
                            {
                                mres.Wait(cts.Token);

                                await streamToWriteTo.WriteAsync(buffer, 0, bytesRead, cts.Token);
                            }
                        }
                    }

                    ActiveDownloads.Remove(item);
                    SucceededDownloads.Add(new DownloadItem { url = item.url, SavePath = item.SavePath });
                }
                catch (OperationCanceledException)
                {
                    ActiveDownloads.Remove(item);
                    FailedDownloads.Add(new DownloadItem { url = item.url, SavePath = item.SavePath });

                    if (File.Exists(item.SavePath) && cts.IsCancellationRequested)
                    {
                        try { File.Delete(item.SavePath); } catch { }
                    }
                }
                catch (Exception ex)
                {
                    ActiveDownloads.Remove(item);
                    FailedDownloads.Add(new DownloadItem { url = item.url, SavePath = item.SavePath });
                }
                finally 
                {
                    if(_ctsDictionary.TryRemove(item.Id, out var usedCts)) usedCts.Dispose();
                    if (_pauseDictionary.TryRemove(item.Id, out var usedMres)) usedMres.Dispose();
                }
            });
        }

        private void StopDownload(object sender, RoutedEventArgs e)
        {
            var selected = CurrentDownloads.SelectedItem as DownloadItem;

            if(selected == null) {MessageBox.Show("Please select a download to stop.", "No Selection", MessageBoxButton.OK, MessageBoxImage.Warning); return; }

            if (_pauseDictionary.TryGetValue(selected.Id, out var mres))
            {
                mres.Set();
            }

            if (_ctsDictionary.TryGetValue(selected.Id, out var cts))
            {
                cts.Cancel();
            }
        }

        private void PauseDownload(object sender, RoutedEventArgs e)
        {
            var selectedItem = CurrentDownloads.SelectedItem as DownloadItem;
            if (selectedItem == null)
            {
                MessageBox.Show("Будь ласка, виберіть активне завантаження для паузи.", "Увага", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_pauseDictionary.TryGetValue(selectedItem.Id, out var mres))
            {
                if (mres.IsSet)
                {
                    mres.Reset(); 
                    MessageBox.Show($"Завантаження '{selectedItem.FileName}' призупинено.", "Пауза");
                }
                else
                {
                    mres.Set(); 
                    MessageBox.Show($"Завантаження '{selectedItem.FileName}' відновлено.", "Робота");
                }
            }
        }
    }
}
