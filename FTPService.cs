using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FTPClient
{
    // 以异步操作封装 FTP 各服务，UI 可直接 await 调用
    partial class FTPService
    {
        public string CurrentRemotePath { get; set; }
        public ServerInfo Server { get; set; }

        // 以下消息通知仅适用于：在长连接(persistentConnection)上的任何操作、获取文件列表(GetFileList()函数)
        public delegate void FTPErrorHandler(FTPResponseException exception);
        public event FTPErrorHandler FTPErrorNotifications;
        public event FTPInfoHandler FTPInfoNotifier;

        public delegate void FTPTaskErrorHandler(FTPResponseException exception, object tag);

        private FTPConnection persistentConnection = null;  // 长连接，不允许在此连接上建立数据传输通道，其工作目录与CurrentRemotePath维持一致。
        private readonly object persistentConnectionLock = new object();

        public Task DownloadFile(FileDownloadingInfo fileDownloadingInfo, CancellationToken token, FTPTaskErrorHandler ftpTaskErrorHandler = null)
        {
            return Task.Run(() =>
            {
                lock (fileDownloadingInfo)
                {
                    if (fileDownloadingInfo.IsFinished) return;
                    FTPConnection ftpConnection = null;
                    try
                    {
                        ftpConnection = CreateConnection(fileDownloadingInfo.serverInfo);
                        Utils.WriteDebugInfo(ftpConnection.connectionName, "用于下载文件的连接已建立。\r\nFileDownloadingInfo: " + fileDownloadingInfo.ToString());
                        ftpConnection.DownloadFile(fileDownloadingInfo, token);
                        ftpConnection.Close();
                        fileDownloadingInfo.Close();
                        fileDownloadingInfo.IsFinished = true;
                        Utils.WriteDebugInfo(ftpConnection.connectionName, "操作已完成，连接主动断开。");
                    }
                    catch (OperationCanceledException)
                    {
                        ftpConnection?.Close();
                        if (ftpConnection != null) Utils.WriteDebugInfo(ftpConnection.connectionName, "操作被用户取消，连接已断开。");
                    }
                    catch (FTPResponseException ex)
                    {
                        ftpTaskErrorHandler?.Invoke(ex, fileDownloadingInfo.Tag);
                        ftpConnection?.Close();
                        if (ftpConnection != null) Utils.WriteDebugInfo(ftpConnection.connectionName, "由于发生了不可恢复的异常，连接已断开。" + " FTPResponseException: " + ex.Message);
                    }
                }
            });
        }

        public Task UploadFile(FileUploadingInfo fileUploadingInfo, CancellationToken token, FTPTaskErrorHandler ftpTaskErrorHandler = null)
        {
            return Task.Run(() =>
            {
                lock (fileUploadingInfo)
                {
                    if (fileUploadingInfo.IsFinished) return;
                    FTPConnection ftpConnection = null;
                    try
                    {
                        ftpConnection = CreateConnection(fileUploadingInfo.serverInfo);
                        Utils.WriteDebugInfo(ftpConnection.connectionName, "用于上传文件的连接已建立。\r\nFileUploadingInfo: " + fileUploadingInfo.ToString());
                        ftpConnection.UploadFile(fileUploadingInfo,token);
                        ftpConnection.Close();
                        fileUploadingInfo.Close();
                        fileUploadingInfo.IsFinished = true;
                        Utils.WriteDebugInfo(ftpConnection.connectionName, "操作已完成，连接主动断开。");
                    }
                    catch (OperationCanceledException)
                    {
                        ftpConnection?.Close();
                        if (ftpConnection != null) Utils.WriteDebugInfo(ftpConnection.connectionName, "操作被用户取消，连接已断开。");
                    }
                    catch (FTPResponseException ex)
                    {
                        ftpTaskErrorHandler?.Invoke(ex, fileUploadingInfo.Tag);
                        ftpConnection?.Close();
                        if (ftpConnection != null) Utils.WriteDebugInfo(ftpConnection.connectionName, "由于发生了不可恢复的异常，连接已断开。" + " FTPResponseException: " + ex.Message);
                    }
                }
            });
        }

        public Task<bool> CreatePersistentConnection()
        {
            return Task.Run(() =>
            {
                try
                {
                    lock (persistentConnectionLock)
                    {
                        persistentConnection?.Close();
                        persistentConnection = CreateConnection(Server, true);
                        CurrentRemotePath = persistentConnection.GetCurrentWorkingDirectory();
                    }
                    timerKeepAlive = new Timer((state) =>
                    {
                        try
                        {
                            lock (persistentConnectionLock)
                            {
                                persistentConnection.SendNullCommand();
                            }
                        }
                        catch (FTPResponseException ex)
                        {
                            timerKeepAlive.Dispose();
                            timerKeepAlive = null;
                            FTPErrorNotifications?.Invoke(ex);
                        }
                    }, null, 15000, 15000);
                    return true;
                }
                catch (FTPResponseException ex)
                {
                    FTPErrorNotifications?.Invoke(ex);
                }
                return false;
            });
        }

        private Timer timerKeepAlive = null;

        private long connectionCount = 0;  // Note: 用于Connection命名，ConnectionName目前只用于日志

        private FTPConnection CreateConnection(ServerInfo server, bool setNotifier = false, string connectionName = null)
        {
            if (connectionName == null) { connectionCount++; connectionName = connectionCount.ToString();  }
            int retryCount = 0;
            while (retryCount <= 3)
            {
                try
                {
                    retryCount++;
                    FTPConnection ftpConnection = new FTPConnection(connectionName, server);
                    if (setNotifier) ftpConnection.FTPInfoNotifier += FTPInfoNotifier;
                    ftpConnection.Connect();
                    return ftpConnection;
                }
                catch (FTPResponseException ex)
                {
                    if (!ex.Recoverable) throw;
                }
            }
            throw new FTPResponseException("客户端无法创建Socket，请检查端口号使用情况");
        }

        public Task<bool> ReturnToParentDirectory()
        {
            return Task.Run(() =>
            {
                try
                {
                    lock (persistentConnectionLock)
                    {
                        persistentConnection.ChangeCurrentWorkingDirectory("..");
                        CurrentRemotePath = persistentConnection.GetCurrentWorkingDirectory();
                    }
                    return true;
                }
                catch (FTPResponseException ex)
                {
                    FTPErrorNotifications?.Invoke(ex);
                }
                return false;
            });
        }

        public Task<bool> ChangeWorkingDirectory(string newDirectory)
        {
            return Task.Run(() =>
            {
                try
                {
                    lock (persistentConnectionLock)
                    {
                        persistentConnection.ChangeCurrentWorkingDirectory(newDirectory);
                        CurrentRemotePath = persistentConnection.GetCurrentWorkingDirectory();
                    }
                    return true;
                }
                catch (FTPResponseException)
                {
                    FTPInfoNotifier?.Invoke("无法切换到目录 " + newDirectory);
                }
                return false;
            });
        }

        public Task<List<RemoteFile>> GetFileList()
        {
            return Task.Run(() =>
            {
                FTPConnection ftpConnection = null;
                try
                {
                    ftpConnection = CreateConnection(Server);
                    Utils.WriteDebugInfo(ftpConnection.connectionName, "用于获取文件列表的连接已建立。");
                    ftpConnection.ChangeCurrentWorkingDirectory(CurrentRemotePath);
                    FTPInfoNotifier?.Invoke("获取文件列表...");
                    string result = ftpConnection.GetFileList(true);
                    var list = ParseFileListWithFileInfo(result);
                    if (list == null)
                    {
                        // fallback to NLST
                        result = ftpConnection.GetFileList(false);
                        list = ParseFileList(ftpConnection, result);
                    }
                    FTPInfoNotifier?.Invoke("FTP 服务就绪，文件列表刷新于 " + DateTime.Now + "，共" + list.Count + "项");
                    ftpConnection.Close();
                    Utils.WriteDebugInfo(ftpConnection.connectionName, "操作已成功完成，连接主动断开。");
                    return list;
                }
                catch (FTPResponseException ex)
                {
                    FTPErrorNotifications?.Invoke(ex);
                    ftpConnection?.Close();
                    if (ftpConnection != null) Utils.WriteDebugInfo(ftpConnection.connectionName, "由于发生了不可恢复的异常，连接已断开。" + " FTPResponseException: " + ex.Message);
                }
                return null;
            });
        }

        private static readonly string patternForUnix = @"^(?<dir>[\-ld])(?<permission>([\-r][\-w][\-xs]){3})\s+(?<filecode>\d+)\s+(?<owner>\S+)\s+(?<group>\S+)\s+(?<size>\d+)\s+(?<timestamp>((?<month>\w{3})\s+(?<day>\d{1,2})\s+(?<hour>\d{1,2}):(?<minute>\d{2}))|((?<month>\w{3})\s+(?<day>\d{1,2})\s+(?<year>\d{4})))\s+(?<name>.+)$";
        private static readonly Regex regexForUnix = new Regex(patternForUnix, RegexOptions.Compiled);

        private List<RemoteFile> ParseFileListWithFileInfo(string filelist)
        {
            List<RemoteFile> list = new List<RemoteFile>();
            string[] files = filelist.Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            bool unix_regex_could_match = true;

            DateTimeFormatInfo usDateTimeFormat = new CultureInfo("en-US", false).DateTimeFormat;
            usDateTimeFormat.ShortTimePattern = "t";

            foreach (var file in files)
            {
                RemoteFile remoteFile = new RemoteFile
                {
                    Size = "",
                    ModifiedTime = "",
                    IsDirectory = false
                };

                if (unix_regex_could_match)
                {
                    Match match = regexForUnix.Match(file);
                    if (!match.Success) unix_regex_could_match = false;
                    else
                    {
                        try
                        {
                            var groups = match.Groups;
                            remoteFile.ModifiedTime = groups["timestamp"].Value;
                            if (groups["dir"].Value != "-") remoteFile.IsDirectory = true;
                            else remoteFile.Size = Utils.SizeToFriendlyString(long.Parse(groups["size"].Value));
                            remoteFile.Name = groups["name"].Value;
                        }
                        catch (Exception)
                        {
                            return null;
                        }
                    }
                }
                
                if (!unix_regex_could_match)
                {
                    
                    // Try windows version
                    string[] groups = file.Split(new string[] { " " }, StringSplitOptions.RemoveEmptyEntries);
                    if (groups.Length < 4) return null;
                    try
                    {
                        remoteFile.ModifiedTime = DateTime.Parse(groups[0] + " " + groups[1], usDateTimeFormat).ToString("yyyy/MM/dd HH:mm:ss");
                        if (groups[2] == "<DIR>") remoteFile.IsDirectory = true;
                        else remoteFile.Size = Utils.SizeToFriendlyString(long.Parse(groups[2]));
                        remoteFile.Name = groups[3];
                        for (int i = 4; i < groups.Length; i++) remoteFile.Name += " " + groups[i];
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }

                list.Add(remoteFile);
            }
            list.Sort((a, b) => { if (a.IsDirectory && !b.IsDirectory) return -1; else if (!a.IsDirectory && b.IsDirectory) return 1; else return a.Name.CompareTo(b.Name); });
            return list;
        }

        private List<RemoteFile> ParseFileList(FTPConnection conn, string filelist)
        {
            List<RemoteFile> list = new List<RemoteFile>();
            string[] files = filelist.Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var file in files)
            {
                RemoteFile remoteFile = new RemoteFile
                {
                    Size = "",
                    ModifiedTime = ""
                };
                var size = conn.GetFileSize(file);
                if (size == -1) { remoteFile.IsDirectory = true; remoteFile.Name = file; }
                else
                {
                    var rawTime = conn.GetFileLastModifiedTime(file);
                    try
                    {
                        remoteFile.ModifiedTime = DateTime.ParseExact(rawTime, "yyyyMMddHHmmss", CultureInfo.CurrentCulture).ToString("yyyy/MM/dd HH:mm:ss");
                        remoteFile.Size = Utils.SizeToFriendlyString(size);
                    }
                    catch (Exception) { }
                    remoteFile.IsDirectory = false; remoteFile.Name = file;
                }
                list.Add(remoteFile);
            }
            return list;
        }
    }

    class RemoteFile
    {
        public bool IsDirectory { get; set; }
        public string Name { get; set; }
        public string Size { get; set; }
        public string ModifiedTime { get; set; }
    }

}
