using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.IO;
using System.Net;
using Microsoft.Win32;
using WebDav;
using System.ComponentModel;

namespace WebDAVBox
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private WebDavClient _client;
        private string _serverUrl = "";
        private string _currentPath = "/";

        public MainWindow()
        {
            InitializeComponent();
            // 訂閱 Loaded 事件，不要在建構子裡直接做網路請求
            this.Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            string url = "";
            string user = "";
            string pass = "";

            try
            {
                _serverUrl = url.EndsWith("/") ? url : url + "/";

                var clientParams = new WebDavClientParams
                {
                    BaseAddress = new Uri(_serverUrl),
                    Credentials = new NetworkCredential(user, pass),
                    PreAuthenticate = true
                };

                _client = new WebDavClient(clientParams);

                // 1. 異步驗證連線（完全不會卡住 UI，也不會引起死鎖）
                TxtStatus.Text = "正在連線至 WebDAV 伺服器...";
                var response = await _client.Propfind("");

                if (response.IsSuccessful)
                {
                    _currentPath = "/";

                    // 2. 異步載入檔案清單（在 UI 執行緒直接 await）
                    await LoadDirectoryAsync(_currentPath);

                    // 3. 更新 UI 控制項（此處本身就是 UI 執行緒，不需要 Dispatcher）
                    BtnGoUp.IsEnabled = true;
                    BtnRefresh.IsEnabled = true;
                    BtnUpload.IsEnabled = true;
                    BtnDelete.IsEnabled = true;
                    TxtStatus.Text = "連線成功";
                    GbServerSettings.Visibility = Visibility.Collapsed;
                }
                else
                {
                    TxtStatus.Text = $"連線失敗: {response.StatusCode}";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"連線或讀取目錄時發生例外: {ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        // 1. 連線至 WebDAV 伺服器并初始化 WebDavClient
        private async void BtnConnect_Click(object sender, RoutedEventArgs e)
        {
            string url = TxtServerUrl.Text.Trim();
            string user = TxtUsername.Text.Trim();
            string pass = TxtPassword.Password;

            if (string.IsNullOrEmpty(url))
            {
                MessageBox.Show("請輸入 WebDAV Server URL！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                TxtStatus.Text = "連線中...";
                BtnConnect.IsEnabled = false;

                _serverUrl = url.EndsWith("/") ? url : url + "/";

                // 初始化 WebDavClient Params
                var clientParams = new WebDavClientParams
                {
                    BaseAddress = new Uri(_serverUrl),
                    Credentials = new NetworkCredential(user, pass),  // 设置网络凭据
                    PreAuthenticate = true // 开启预认证（在首次请求时即附带认证信息，防止 401 交互失败）
                };

                // 2. 如果服务器不支持 PreAuthenticate 挑战，可手主动设置 Basic Authorization 标头
                // 3. 手動構建預設請求頭（以防部分 WebDAV 服務器未能觸發 Challenge 驗證）
                //var authBytes = Encoding.UTF8.GetBytes($"{user}:{pass}");
                //var base64Auth = Convert.ToBase64String(authBytes);
                //clientParams.DefaultRequestHeaders = new Dictionary<string, string>
                //{
                //    { "Authorization", $"Basic {base64Auth}" }
                //};

                _client = new WebDavClient(clientParams);

                var response = await _client.Propfind("");

                if (response.IsSuccessful)
                {
                    // 載入 WebDAV 檔案清單
                    _currentPath = "/";
                    await LoadDirectoryAsync(_currentPath);

                    BtnGoUp.IsEnabled = true;
                    BtnRefresh.IsEnabled = true;
                    BtnUpload.IsEnabled = true;
                    TxtStatus.Text = "連線成功";

                    // 💡 關鍵步驟：連接成功後，隱藏整個 GroupBox
                    GbServerSettings.Visibility = Visibility.Collapsed;
                }
                else
                {
                    MessageBox.Show($"連接失敗：{response.StatusCode} {response.Description}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                    TxtStatus.Text = "連接失敗";
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"連線失敗: {ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtStatus.Text = "連線失敗";
            }
            finally
            {
                BtnConnect.IsEnabled = true;
            }
        }

        // 2. 異步載入指定目錄下的檔案清單 (使用 Propfind)
        private async Task LoadDirectoryAsync(string path)
        {
            try
            {
                TxtStatus.Text = $"正在載入: {path}";

                // 移除開頭的斜線，建立相對 Uri
                string relativePath = path.StartsWith("/") ? path.Substring(1) : path;

                // 發起 WebDAV Propfind 請求
                PropfindResponse response = await _client.Propfind(relativePath);
              
                if (!response.IsSuccessful)
                {
                    MessageBox.Show($"讀取目錄失敗 (HTTP {response.StatusCode}): {response.Description}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                    TxtStatus.Text = "讀取失敗";
                    return;
                }

                var fileList = new List<FileItemViewModel>();

                // 解析回應中的 Resources
                foreach (WebDavResource resource in response.Resources)
                {
                    // WebDAV 會在第一個資源中返回當前請求的目錄本身，需予以過濾
                    string resourceUri = Uri.UnescapeDataString(resource.Uri);
                    string targetUri = Uri.UnescapeDataString(new Uri(new Uri(_serverUrl), relativePath).AbsolutePath);

                    if (resourceUri.TrimEnd('/') == targetUri.TrimEnd('/'))
                        continue;

                    // 提取檔案/資料夾名稱
                    string itemName = GetResourceName(resource);

                    fileList.Add(new FileItemViewModel
                    {
                        Name = itemName,
                        Path = resource.Uri,
                        IsCollection = resource.IsCollection,
                        ContentLength = resource.ContentLength ?? 0,
                        LastModified = (resource.LastModifiedDate ?? DateTime.MinValue).ToString("yyyy/MM/dd HH:mm:ss")
                    });
                }

                // 資料夾排在前面，按名稱排序
                fileList = fileList
                    .OrderByDescending(i => i.IsCollection)
                    .ThenBy(i => i.Name)
                    .ToList();

                DgFiles.ItemsSource = fileList;
                _currentPath = path;
                //TxtCurrentPath.Text = _currentPath;
                TxtStatus.Text = $"已載入 {fileList.Count} 個項目";

                // 💡 ウィンドウの Title も一緒に更新！
                Title = $"💡當前路徑:  {path}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"讀取目錄時發生例外: {ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                TxtStatus.Text = "讀取失敗";
            }
        }

        // 輔助方法：解析資源名稱
        private string GetResourceName(WebDavResource resource)
        {
            if (!string.IsNullOrEmpty(resource.DisplayName))
                return resource.DisplayName;

            string uriStr = Uri.UnescapeDataString(resource.Uri).TrimEnd('/');
            int lastSlash = uriStr.LastIndexOf('/');
            return lastSlash >= 0 ? uriStr.Substring(lastSlash + 1) : uriStr;
        }

        // 3. 雙擊進入資料夾
        private async void DgFiles_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DgFiles.SelectedItem is FileItemViewModel selectedItem && selectedItem.IsCollection)
            {
                // 取得相對路徑
                Uri baseUri = new Uri(_serverUrl);
                Uri itemUri = new Uri(baseUri, selectedItem.Path);
                string relativePath = baseUri.MakeRelativeUri(itemUri).ToString();

                if (!relativePath.StartsWith("/"))
                    relativePath = "/" + relativePath;

                await LoadDirectoryAsync(relativePath);
            }
        }

        // 4. 返回上一層目錄
        private async void BtnGoUp_Click(object sender, RoutedEventArgs e)
        {
            if (_currentPath == "/" || string.IsNullOrEmpty(_currentPath))
                return;

            string trimmedPath = _currentPath.TrimEnd('/');
            int lastSlashIndex = trimmedPath.LastIndexOf('/');

            string parentPath = "/";
            if (lastSlashIndex > 0)
            {
                parentPath = trimmedPath.Substring(0, lastSlashIndex + 1);
            }

            await LoadDirectoryAsync(parentPath);
        }

        // 5. 重新整理當前目錄
        private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
        {
            await LoadDirectoryAsync(_currentPath);
        }

        // 7. 下載檔案 (使用 RawHttpGet)
        private async void BtnDownloadItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is FileItemViewModel item && !item.IsCollection)
            {
                SaveFileDialog saveFileDialog = new SaveFileDialog
                {
                    FileName = item.Name
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    try
                    {
                        TxtStatus.Text = $"下載中: {item.Name}...";

                        // 轉換為相對路徑
                        Uri baseUri = new Uri(_serverUrl);
                        Uri itemUri = new Uri(baseUri, item.Path);
                        string relativePath = baseUri.MakeRelativeUri(itemUri).ToString();

                        // 使用 GetRawFile 發起 GET 請求下載資源串流
                        WebDavStreamResponse response = await _client.GetRawFile(relativePath);

                        if (response.IsSuccessful)
                        {
                            using (Stream remoteStream = response.Stream)
                            using (FileStream localStream = File.Create(saveFileDialog.FileName))
                            {
                                await remoteStream.CopyToAsync(localStream);
                            }

                            MessageBox.Show("下載完成！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                            TxtStatus.Text = "下載完成";
                        }
                        else
                        {
                            MessageBox.Show($"下載失敗 (HTTP {response.StatusCode}): {response.Description}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                            TxtStatus.Text = "下載失敗";
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"下載過程發生錯誤: {ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                        TxtStatus.Text = "下載失敗";
                    }
                }
            }
        }

        // 6. 上傳檔案 (使用 PutFile)
        private async void BtnUpload_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog openFileDialog = new OpenFileDialog
            {
                Multiselect = true // 順便開啟多選上傳支援
            };

            if (openFileDialog.ShowDialog() == true)
            {
                BtnUpload.IsEnabled = false;
                int successCount = 0;

                foreach (string fileName in openFileDialog.FileNames)
                {
                    bool success = await UploadSingleFileAsync(fileName);
                    if (success) successCount++;
                }

                MessageBox.Show($"上傳完成，成功上傳 {successCount} 個檔案！", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                await LoadDirectoryAsync(_currentPath);
                BtnUpload.IsEnabled = true;
            }
        }



        // 6. 删除檔案  
        private async void BtnDelete_Click(object sender, RoutedEventArgs e)
        {
            // DataGrid で選択されている項目を取得 (FileItem クラス名は実際の環境に合わせて変更してください)
            var selectedItem = DgFiles.SelectedItem as FileItemViewModel;  //  FileItem WebDavItemViewModel

            if (selectedItem == null)
            {
                MessageBox.Show("請先選擇要刪除的檔案或資料夾。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 削除の確認ダイアログ
            var result = MessageBox.Show($"確定要刪除 [{selectedItem.Name}] 嗎？", "確認刪除",
                                         MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                TxtStatus.Text = $"正在刪除 {selectedItem.Name}...";

                // WebDAV サーバーへの削除リクエスト (絶対パスまたは相対パスを指定)
                string targetPath = selectedItem.Path; // 例: "/folder/file.txt"
                var response = await _client.Delete(targetPath);

                if (response.IsSuccessful)
                {
                    TxtStatus.Text = "刪除成功";
                    // ディレクトリ一覧を再読み込みして最新状態にする
                    await LoadDirectoryAsync(_currentPath);
                }
                else
                {
                    TxtStatus.Text = $"刪除失敗: {response.StatusCode}";
                    MessageBox.Show($"刪除失敗，錯誤碼: {response.StatusCode}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"刪除時發生例外: {ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        #region 拖放上傳功能 (Drag & Drop)
        // 1. 當鼠標拖著檔案移入 DataGrid 區域時觸發
        private void DgFiles_DragOver(object sender, DragEventArgs e)
        {
            // 檢查拖放的內容是否包含檔案/資料夾路徑
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy; // 顯示複製/加號游標
            }
            else
            {
                e.Effects = DragDropEffects.None; // 顯示禁止游標
            }
            e.Handled = true; // 阻止事件繼續冒泡
        }

        // 2. 當使用者放開鼠標（Drop）時觸發
        private async void DgFiles_Drop(object sender, DragEventArgs e)
        {
            // 確保已建立 WebDAV 連線
            if (_client == null)
            {
                MessageBox.Show("請先建立 WebDAV 伺服器連線！", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                // 獲取所有拖入的檔案或資料夾完整路徑陣列
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);

                if (files == null || files.Length == 0)
                    return;

                int successCount = 0;
                int failCount = 0;

                try
                {
                    BtnUpload.IsEnabled = false;

                    foreach (string fileOrFolderPath in files)
                    {
                        // 如果是檔案直接上傳
                        if (File.Exists(fileOrFolderPath))
                        {
                            bool success = await UploadSingleFileAsync(fileOrFolderPath);
                            if (success) successCount++; else failCount++;
                        }
                        // 如果是資料夾，可以遞迴上傳（以下示範簡單跳過或提示，也可自行擴充遞迴上傳）
                        else if (Directory.Exists(fileOrFolderPath))
                        {
                            //MessageBox.Show($"目前暫不支援直接拖入資料夾：{Path.GetFileName(fileOrFolderPath)}", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                            var (folderSuccess, folderFail) = await UploadFolderAsync(fileOrFolderPath, _currentPath);
                            successCount += folderSuccess;
                            failCount += folderFail;
                        }
                    }

                    // 拖放完成後重新整理目錄
                    await LoadDirectoryAsync(_currentPath);

                    TxtStatus.Text = $"拖放上傳完成：成功 {successCount} 個，失敗 {failCount} 個";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"拖放上傳過程發生例外: {ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    BtnUpload.IsEnabled = true;
                }
            }
        }

        // 3. 封裝單檔案上傳的核心方法
        private async Task<bool> UploadSingleFileAsync(string localFilePath)
        {
            string fileName = Path.GetFileName(localFilePath);
            try
            {
                TxtStatus.Text = $"正在上傳: {fileName}...";

                string destRelativePath = _currentPath.TrimStart('/');
                if (!destRelativePath.EndsWith("/") && !string.IsNullOrEmpty(destRelativePath))
                    destRelativePath += "/";

                destRelativePath += fileName;

                using (FileStream fileStream = File.OpenRead(localFilePath))
                {
                    // 調用 WebDav.Client 的 PutFile API
                    WebDavResponse response = await _client.PutFile(destRelativePath, fileStream);
                    return response.IsSuccessful;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"上傳檔案 {fileName} 失敗: {ex.Message}");
                return false;
            }
        }
        #endregion



        /// <summary>
        /// 遞迴上傳本地資料夾及其所有子內容至 WebDAV 伺服器
        /// </summary>
        /// <param name="localFolderPath">本地資料夾完整路徑 (例如: C:\Users\Test\MyFolder)</param>
        /// <param name="targetRemoteParentPath">遠端目標父目錄路徑 (例如: / 或 /Documents/)</param>
        private async Task<(int success, int fail)> UploadFolderAsync(string localFolderPath, string targetRemoteParentPath)
        {
            int successCount = 0;
            int failCount = 0;

            try
            {
                string folderName = Path.GetFileName(localFolderPath);

                // 1. 組合該資料夾在 WebDAV 上的遠端相對路徑 (確保以 '/' 結尾)
                string remoteFolderPath = CombineWebDavPath(targetRemoteParentPath, folderName) + "/";

                TxtStatus.Text = $"正在建立遠端目錄: {remoteFolderPath}...";

                // 2. 在 WebDAV 上建立目錄 (Mkcol)
                // 註：若目錄已存在，WebDAV Mkcol 通常會回傳 405 (Method Not Allowed) 或成功，可靈活處置
                WebDavResponse mkcolResponse = await _client.Mkcol(remoteFolderPath.TrimStart('/'));

                // 3. 上傳當前資料夾下的所有檔案
                string[] files = Directory.GetFiles(localFolderPath);
                foreach (string filePath in files)
                {
                    bool isUploaded = await UploadSingleFileToPathAsync(filePath, remoteFolderPath);
                    if (isUploaded) successCount++; else failCount++;
                }

                // 4. 遞迴處理所有子資料夾
                string[] subDirectories = Directory.GetDirectories(localFolderPath);
                foreach (string subDirPath in subDirectories)
                {
                    var (subSuccess, subFail) = await UploadFolderAsync(subDirPath, remoteFolderPath);
                    successCount += subSuccess;
                    failCount += subFail;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"上傳資料夾 {localFolderPath} 失敗: {ex.Message}");
                failCount++;
            }

            return (successCount, failCount);
        }

        /// <summary>
        /// 支援指定自訂遠端目標目錄的單檔案上傳方法
        /// </summary>
        private async Task<bool> UploadSingleFileToPathAsync(string localFilePath, string remoteTargetDirectoryPath)
        {
            string fileName = Path.GetFileName(localFilePath);
            try
            {
                TxtStatus.Text = $"正在上傳: {fileName}...";

                // 組合完整的遠端檔案路徑
                string remoteFilePath = CombineWebDavPath(remoteTargetDirectoryPath, fileName);

                using (FileStream fileStream = File.OpenRead(localFilePath))
                {
                    WebDavResponse response = await _client.PutFile(remoteFilePath.TrimStart('/'), fileStream);
                    return response.IsSuccessful;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"上傳檔案 {fileName} 失敗: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 安全拼接 WebDAV URL 路徑的輔助方法
        /// </summary>
        private string CombineWebDavPath(string basePath, string relativeName)
        {
            if (string.IsNullOrEmpty(basePath) || basePath == "/")
                return "/" + relativeName;

            return basePath.TrimEnd('/') + "/" + relativeName;
        }
    }


    // ViewModel 擴充模型
    public class FileItemViewModel : INotifyPropertyChanged
    {
        // リソース名 (例: "documents", "report.pdf")
        public string Name { get; set; }

        // パス (例: "/documents/report.pdf")
        public string Path { get; set; }

        // フォルダかどうか
        public bool IsCollection { get; set; }

        // アイコン表示用 (例: 📁 または 📄)
        public string TypeDisplay => IsCollection ? "📁 資料夾" : "📄 檔案";

        // サイズ (バイトまたは整形済み文字列)
        public long? ContentLength { get; set; }

        // 最終更新日時
        public string LastModified { get; set; }

        // フォルダでなければ Visibility.Visible を返すプロパティを追加
        public Visibility DownloadButtonVisibility => IsCollection ? Visibility.Collapsed : Visibility.Visible;


        // --- INotifyPropertyChanged の実装 ---
        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged(string propertyName) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

}