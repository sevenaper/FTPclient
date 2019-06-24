using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Threading;
using System.IO;

namespace FTPClient
{
    /// <summary>
    /// 涉及到 FTP服务 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly BindingList<RemoteFileInfoUI> remoteFileList = new BindingList<RemoteFileInfoUI>();
        private readonly BindingList<TaskInfoUI> taskInfoList = new BindingList<TaskInfoUI>();

        private FTPService ftpService = new FTPService();

        // 指明 PersistentConnection 的状态，与其他连接无关
        enum ConnectionStatus { Closed, Connecting, Connected };
        private ConnectionStatus connectionStatus = ConnectionStatus.Closed;

        private void DefaultFTPErrorHandler(FTPResponseException exception)
        {
            if (exception.Recoverable)
            {
                Dispatcher.Invoke(new Action(() => {
                    TextStatus.Text = exception.Message;
                }));
                return;
            }
            Dispatcher.Invoke(new Action(() => {
                TextStatus.Text = exception.Message + "\r\n连接没有成功或连接已断开，请重新连接。";
                connectionStatus = ConnectionStatus.Closed; ButtonConnect.Content = "连接";
            }));
        }

        private void DefaultFTPInfoHandler(string message)
        {
            Dispatcher.Invoke(new Action(() => {
                TextStatus.Text = message;
            }));
        }

        private void DefaultFTPTaskErrorHandler(FTPResponseException exception, object tag)
        {
            Dispatcher.Invoke(new Action(() => {
                if (tag == null || tag.GetType() != typeof(TaskInfoUI)) return;
                var fi = tag as TaskInfoUI;
                fi.Message = exception.Message;
                fi.IsErrorHappened = true;
            }));
        }

        private void UploadLocalFile(string localFileName, string remoteFileName)
        {
            UploadLocalFile(localService.CurrentLocalPath, localFileName, ftpService.CurrentRemotePath, remoteFileName);
        }

        private async void UploadLocalFile(string localFilePath, string localFileName, string remoteFilePath, string remoteFileName)
        {
            if (connectionStatus != ConnectionStatus.Connected) return;
            var fui = new FileUploadingInfo(ftpService.Server, localFilePath, localFileName, remoteFilePath, remoteFileName);
            TaskInfoUI tiUI = new TaskInfoUI(fui);
            fui.Tag = tiUI;
            tiUI.tokenSource = new CancellationTokenSource();
            taskInfoList.Add(tiUI);
            await ftpService.UploadFile(fui, tiUI.tokenSource.Token, DefaultFTPTaskErrorHandler);
            if (fui.IsFinished) RefreshRemoteInfo();
        }
       

        private async void DownloadRemoteFile(string remoteFileName, string localFileName)
        {
            if (connectionStatus != ConnectionStatus.Connected) return;
            var fdi = new FileDownloadingInfo(ftpService.Server, ftpService.CurrentRemotePath, remoteFileName, localService.CurrentLocalPath, localFileName);
            TaskInfoUI tiUI = new TaskInfoUI(fdi);
            fdi.Tag = tiUI;
            tiUI.tokenSource = new CancellationTokenSource();
            taskInfoList.Add(tiUI);
            await ftpService.DownloadFile(fdi, tiUI.tokenSource.Token, DefaultFTPTaskErrorHandler);
            if (fdi.IsFinished) Dispatcher.Invoke(new Action(() => { RefreshLocalInfo(); }));
        }

        private async void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (connectionStatus == ConnectionStatus.Closed)
            {
                connectionStatus = ConnectionStatus.Connecting;
                ServerInfo server = new ServerInfo();
                server.ServerIP = TextServerIP.Text;
                try
                {
                    server.ServerPort = int.Parse(TextServerPort.Text);
                }
                catch (Exception)
                {
                    DefaultFTPErrorHandler(new FTPResponseException("端口号不正确。"));
                    return;
                }
                server.Username = TextUsername.Text;
                server.Password = TextPassword.Password;
                ftpService.Server = server;
                ftpService.FTPInfoNotifier += DefaultFTPInfoHandler;
                ftpService.FTPErrorNotifications += DefaultFTPErrorHandler;
                var succeeded = await ftpService.CreatePersistentConnection();
                if (!succeeded) return;
                var result = await ftpService.GetFileList();
                if (result == null) return;
                Dispatcher.Invoke(new Action(() => {
                    remoteFileList.Clear();
                    foreach (var item in result) remoteFileList.Add(new RemoteFileInfoUI(item));
                    TextRemotePath.Text = ftpService.CurrentRemotePath;
                    connectionStatus = ConnectionStatus.Connected; ButtonConnect.Content = "断开";
                }));
            }
            else if (connectionStatus == ConnectionStatus.Connected)
            {
                ftpService = new FTPService();
                ButtonConnect.Content = "连接";
                connectionStatus = ConnectionStatus.Closed;
                remoteFileList.Clear();
                DefaultFTPInfoHandler("就绪");
            }
        }

        private async void RefreshRemoteInfo()
        {
            if (connectionStatus != ConnectionStatus.Connected) return;
            var result = await ftpService.GetFileList();
            Dispatcher.Invoke(new Action(() => {
                remoteFileList.Clear();
                if (result != null) foreach (var item in result) remoteFileList.Add(new RemoteFileInfoUI(item));
                TextRemotePath.Text = ftpService.CurrentRemotePath;
            }));
        }

        private async void TaskResumeOrPause_Click(object sender, RoutedEventArgs e)
        {
            // Task操作与PersistentConnection独立，不需要判断ConnectionStatus
            if (ListViewStatus.SelectedIndex == -1) return;
            var fi = taskInfoList[ListViewStatus.SelectedIndex];
            if (fi.IsPaused || fi.IsErrorHappened)
            {
                fi.IsPaused = false;
                fi.IsErrorHappened = false;   // 允许在发生错误后重试
                fi.tokenSource = new CancellationTokenSource();
                if (fi.IsDownloadTask)
                {
                    await ftpService.DownloadFile(fi.FDI, fi.tokenSource.Token, DefaultFTPTaskErrorHandler);
                    if (fi.FDI.IsFinished) Dispatcher.Invoke(new Action(() => { RefreshLocalInfo(); }));
                }
                else
                {
                    await ftpService.UploadFile(fi.FUI, fi.tokenSource.Token, DefaultFTPTaskErrorHandler);
                    if (fi.FUI.IsFinished) RefreshRemoteInfo();
                }
            }
            else
            {
                fi.IsPaused = true;
                fi.tokenSource.Cancel();
            }
        }

        private void TaskDelete_Click(object sender, RoutedEventArgs e)
        {
            var fi = taskInfoList[ListViewStatus.SelectedIndex];
            if (!fi.IsPaused)
            {
                fi.tokenSource.Cancel();
            }
            taskInfoList.Remove(fi);
        }

        private async void ReturnToParentDirRemote_Click(object sender, RoutedEventArgs e)
        {
            if (connectionStatus != ConnectionStatus.Connected) return;
            await ftpService.ReturnToParentDirectory();
            RefreshRemoteInfo();
        }

        private void RefreshRemote_Click(object sender, RoutedEventArgs e)
        {
            if (connectionStatus != ConnectionStatus.Connected) return;
            RefreshRemoteInfo();
        }

        private async void ChangeWorkingDirRemote_KeyDown(object sender, KeyEventArgs e)
        {
            if (connectionStatus != ConnectionStatus.Connected) return;
            if (e.Key == Key.Enter)
            {
                await ftpService.ChangeWorkingDirectory(TextRemotePath.Text);
                RefreshRemoteInfo();
            }
        }

        private class RemoteFileInfoUI : INotifyPropertyChanged
        {
            private readonly RemoteFile remoteFile;
            public RemoteFileInfoUI(RemoteFile file)
            {
                remoteFile = file;
            }
            public bool IsDirectory { get { return remoteFile.IsDirectory; } }
            public string Name {
                get { if (name == null) return remoteFile.Name; else return name; }
                set { name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Name")); }
            }
            private string name = null;
            public string Size { get { return remoteFile.Size; } }
            public string ModifiedTime { get { return remoteFile.ModifiedTime; } }
            public event PropertyChangedEventHandler PropertyChanged;
            public bool IsRenaming
            {
                get { return isRenaming; }
                set { isRenaming = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsRenaming")); }
            }
            private bool isRenaming = false;
        }

        private class TaskInfoUI : INotifyPropertyChanged
        {
            public bool IsDownloadTask { get; set; }   // 1表示下载任务，0表示上传任务
            public bool IsFinished { get { return IsDownloadTask ? FDI.IsFinished : FUI.IsFinished; } }
            public CancellationTokenSource tokenSource;

            public TaskInfoUI(FileDownloadingInfo fdi)
            {
                IsDownloadTask = true;
                FDI = fdi;
                FDI.FTPDownloadingInfoNotifications += (FileDownloadingInfo source, string[] msgs) => {
                    if (msgs.Length > 0) Message = msgs[0];
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Progress"));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ProgressMsg"));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsIndeterminate"));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("FileSize"));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Speed"));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("TimeRemaining"));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Status"));
                };
                stopwatch.Start();
            }

            public TaskInfoUI(FileUploadingInfo fui)
            {
                IsDownloadTask = false;
                FUI = fui;
                FUI.FTPUploadingInfoNotifications += (FileUploadingInfo source, string[] msgs) => {
                    if (msgs.Length > 0) Message = msgs[0];
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Progress"));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("ProgressMsg"));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("IsIndeterminate"));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("FileSize"));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Speed"));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("TimeRemaining"));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Status"));
                };
                stopwatch.Start();
            }

            public readonly FileDownloadingInfo FDI;
            public readonly FileUploadingInfo FUI;
            public event PropertyChangedEventHandler PropertyChanged;

            public string FileName { get { return IsDownloadTask ? FDI.RemoteFileName : FUI.RemoteFileName; } }
            public string SourcePath { get { return IsDownloadTask ? FDI.RemotePath : FUI.LocalPath; } }
            public string DstPath { get { return IsDownloadTask ? FDI.LocalPath : FUI.RemotePath; } }
            public bool IsIndeterminate { get { return IsDownloadTask ? FDI.SavedSize == -1 || FDI.DownloadedBytes == 0 : FUI.FileSize == -1 || FUI.UploadedBytes == 0; } }
            public string FileSize { get {
                    if ((IsDownloadTask ? FDI.SavedSize : FUI.FileSize) == -1) return "等待计算...";
                    return IsDownloadTask ? Utils.SizeToFriendlyString(FDI.SavedSize) : Utils.SizeToFriendlyString(FUI.FileSize); }
            }
            public string Progress
            {
                get
                {
                    if (IsFinished) return "100";
                    if (IsIndeterminate) return "0";
                    if (IsDownloadTask)
                    {
                        return ((int)((double)FDI.DownloadedBytes / (double)FDI.SavedSize * 100.0)).ToString();
                    }
                    else
                    {
                        return ((int)((double)FUI.UploadedBytes / (double)FUI.FileSize * 100.0)).ToString();
                    }
                }
                set { }
            }
            public string ProgressMsg
            {
                get
                {
                    if (IsIndeterminate) return "未知或无法计算...";
                    return IsDownloadTask ? Utils.SizeToFriendlyString(FDI.DownloadedBytes) + " / " + Utils.SizeToFriendlyString(FDI.SavedSize) :
                        Utils.SizeToFriendlyString(FUI.UploadedBytes) + "/" + Utils.SizeToFriendlyString(FUI.FileSize);
                }
            }
            public string Status
            {
                get
                {
                    if (IsErrorHappened) return "发生错误";
                    if (IsFinished) return "已完成";
                    if (IsIndeterminate) return "等待响应...";
                    if (IsPaused) return IsDownloadTask ? "下载已暂停." : "上传已暂停.";
                    return IsDownloadTask ? "下载中..." : "上传中...";
                }
            }

            public string Speed
            {
                get
                {
                    return Utils.SpeedToFriendlyString(CalculateSpeed());
                }
            }
            public string TimeRemaining
            {
                get
                {
                    return Utils.TimeToFriendlyString(CalculateRemainingTime());
                }
            }

            private long ProcessedBytes { get { return IsDownloadTask ? FDI.DownloadedBytes : FUI.UploadedBytes; } }
            private readonly Stopwatch stopwatch = new Stopwatch();

            private long CalculateSpeed()
            {
                if (stopwatch.ElapsedMilliseconds == 0) return 0;
                return (ProcessedBytes * 1000 / stopwatch.ElapsedMilliseconds);
            }

            private long CalculateRemainingTime()
            {
                var speed = CalculateSpeed();
                if (speed == 0) return -1;
                var size = (IsDownloadTask ? FDI.SavedSize : FUI.FileSize) - ProcessedBytes;
                return size / speed;
            }

            public string Message
            {
                get { return message; }
                set { message = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Message")); }
            }
            private string message;

            public bool IsPaused
            {
                get { return isPaused; }
                set { isPaused = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Status")); }
            }
            private bool isPaused = false;

            public bool IsErrorHappened
            {
                get { return isErrorHappened; }
                set { isErrorHappened = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Status")); }
            }
            private bool isErrorHappened = false;
        }
    }
}
