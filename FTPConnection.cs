using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FTPClient
{
    /// <summary>
    /// FTPConnection 的每个实例表示一个 FTP 连接，封装了与FTP服务器交互的接口
    /// </summary>

    partial class FTPConnection
    {
        private readonly Socket cmdSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        private readonly Socket dataSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        private readonly ServerInfo serverInfo;
        public readonly string connectionName;

        public event FTPInfoHandler FTPInfoNotifier;

        public FTPConnection(string name, ServerInfo server)
        {
            connectionName = name;
            serverInfo = server;
        }

        public void Close()
        {
            try
            {
                SendCommand("QUIT");
                ReceiveRawResponse();
            }
            catch (Exception) { }
            try
            {
                cmdSocket?.Close();
                dataSocket?.Close();
            }
            catch (Exception) { }
        }

        public void Connect()
        {
            try
            {
                cmdSocket.ReceiveTimeout = 10000;
                cmdSocket.SendTimeout = 10000;
                // 如 IIS FTP 服务端会在数据通道过多(大于2)时 Pending，故不设超时时间，用户可随时取消上传/下载任务
                dataSocket.ReceiveTimeout = 0;
                dataSocket.SendTimeout = 0;

                FTPInfoNotifier?.Invoke("等待服务器响应...");
                IAsyncResult result = null;
                try
                {
                    result = cmdSocket.BeginConnect(new IPEndPoint(IPAddress.Parse(serverInfo.ServerIP), serverInfo.ServerPort), null, null);
                } catch (Exception ex)
                {
                    throw new FTPResponseException(ex.Message);
                }
                if (result == null) throw new FTPResponseException("无法建立连接。");
                result.AsyncWaitHandle.WaitOne(15000, true);
                if (!result.IsCompleted)
                {
                    cmdSocket.Close();
                    throw new FTPResponseException("由于连接方在一段时间后没有正确答复或连接的主机没有反应，连接尝试失败");
                }

                // 打开客户端接收数据的端口，端口号为客户端控制端口号+1
                var localEndPoint = ((IPEndPoint)cmdSocket.LocalEndPoint);
                try
                {
                    dataSocket.Bind(new IPEndPoint(IPAddress.Parse(localEndPoint.Address.ToString()), localEndPoint.Port + 1));
                } catch (Exception)
                {
                    throw new FTPResponseException("Data Socket 绑定失败", true);  // 可能端口号+1被占用，允许自动重新创建 FTPConnection 以自动重试
                }

                // 等待服务器响应就绪 220
                var msg = WaitResponse(220);

                SendCommand("OPTS UTF8 ON");
                msg = ReceiveRawResponse();

                SendCommand("USER " + serverInfo.Username);
                var msgs = ReceiveResponse(331, 230);
                if (msgs.ContainsKey(331))
                {
                    SendCommand("PASS " + serverInfo.Password);
                    try
                    {
                        msgs = ReceiveResponse(230);
                    } catch (FTPResponseException) { }
                }
                if (!msgs.ContainsKey(230))
                {
                    // 密码错误
                    cmdSocket.Disconnect(true);
                    throw new FTPResponseException("用户名或密码错误");
                }

                FTPInfoNotifier?.Invoke("FTP 服务就绪");
            }
            catch (SocketException ex)
            {
                throw new FTPResponseException(ex.Message);
            }
            catch (FormatException fe)
            {
                throw new FTPResponseException(fe.Message);
            }
            catch (ArgumentOutOfRangeException ee)
            {
                throw new FTPResponseException(ee.Message);
            }
        }

        private void EnterPassiveMode()
        {
            try
            {
                // 启用 Binary Mode
                SendCommand("TYPE I");
                var msg = ReceiveRawResponse();
                if (!msg.StartsWith("2"))
                {
                    cmdSocket.Disconnect(true);
                    throw new FTPResponseException("服务器不支持二进制模式，无法进一步传输数据");
                }
                // 进入被动模式
                SendCommand("PASV");
                msg = ReceiveRawResponse();
                if (!msg.StartsWith("227"))
                {
                    // 服务器不支持被动模式，错误处理
                    cmdSocket.Disconnect(true);
                    throw new FTPResponseException("服务器不支持被动模式，无法进一步传输数据");
                }
                int server_data_port = -1;   // Unspecified
                try
                {
                    // 解析被动模式下服务器数据端口 (127,0,0,1,74,93) 74*256+93
                    int le = msg.LastIndexOf("(");
                    int re = msg.LastIndexOf(")");
                    msg = msg.Substring(le + 1, re - le - 1);
                    string[] da = msg.Split(',');
                    int port = int.Parse(da[da.Length - 2]) * 256 + int.Parse(da[da.Length - 1]);
                    server_data_port = port;
                }
                catch (Exception)
                {
                    cmdSocket.Disconnect(true);
                    throw new FTPResponseException("Server 对 Passive Mode 的响应数据有误");
                }
                dataSocket.ConnectAsync(new IPEndPoint(IPAddress.Parse(serverInfo.ServerIP), server_data_port)).Wait();
            }
            catch (AggregateException ae)
            {
                throw new FTPResponseException(ae.InnerException.Message);
            }
            catch (SocketException ex)
            {
                throw new FTPResponseException(ex.Message);
            }
            catch (FormatException fe)
            {
                throw new FTPResponseException(fe.Message);
            }
            catch (ArgumentOutOfRangeException ee)
            {
                throw new FTPResponseException(ee.Message);
            }
        }

        /// <summary>
        /// 获取未经处理的文件列表数据
        /// </summary>
        /// <param name="listFileInfo">是否用LIST命令取代NLST命令</param>
        /// <returns></returns>
        public string GetFileList(bool listFileInfo = false)
        {
            try
            {
                EnterPassiveMode();
                dataSocket.ReceiveBufferSize = 1 * 1024 * 1024;
                SendCommand(listFileInfo ? "LIST" :"NLST");

                // 等待服务器响应 125 Data connection already open 或 150 about to open data connection
                bool response226Ahead = false; // 是否与125,150一同收到了226消息
                var msgs = ReceiveResponse(125, 150, 226);
                if (msgs == null) throw new FTPResponseException("远程主机对于 NLST 命令没有预期的响应或超时");
                if (msgs.ContainsKey(226)) response226Ahead = true;

                MemoryStream ms = new MemoryStream();
                byte[] buf = new byte[1 * 1024 * 1024];
                int length = 0;

                while (dataSocket.Available > 0 || (length >= 1 && buf[length - 1] != '\n'))
                {
                    length = dataSocket.Receive(buf);
                    // Connected 只反映上一个 Receive/Send 操作时的 Socket 状态
                    if (dataSocket.Connected == false) throw new FTPResponseException("远程主机断开连接");
                    ms.Write(buf, 0, length);
                }

                // 发送FIN
                dataSocket.Shutdown(SocketShutdown.Send);

                // 接收所有剩余的数据，直到Receive返回0，表明对方已发送FIN
                while (true)
                {
                    length = dataSocket.Receive(buf);
                    if (length == 0) break;
                    ms.Write(buf, 0, length);
                }

                // 等待服务器响应 226 Directory send OK.
                if (!response226Ahead) WaitResponse(226);

                dataSocket.Disconnect(true);

                var msg = Encoding.UTF8.GetString(ms.ToArray());

                // 确保数据以\r\n结束
                if (msg != "" && !msg.EndsWith("\r\n"))
                {
                    throw new FTPResponseException("Server 对 LIST/NLST 命令的响应数据有误");
                }
                return msg;
            } catch (SocketException ex)
            {
                throw new FTPResponseException(ex.Message);
            }
        }

        /// <summary>
        /// 下载文件，过程可以暂停，注意：如果本地文件已存在，会覆盖对应文件，调用者应当检查文件是否已存在并给出提示
        /// </summary>
        public void DownloadFile(FileDownloadingInfo fdi, CancellationToken token)
        {
            try
            {
                ChangeCurrentWorkingDirectory(fdi.RemotePath);
                var time = GetFileLastModifiedTime(fdi.RemoteFileName);
                if (time == null) throw new FTPResponseException("无法获取远程文件的最后修改日期，或该远程文件已经不存在");
                var remote_size = GetFileSize(fdi.RemoteFileName);
                if (remote_size == -1) throw new FTPResponseException("无法获取远程文件的文件大小，或该远程文件已经不存在");

                string localfile = fdi.LocalPath + (fdi.LocalPath.EndsWith("\\") ? "" : "\\") + fdi.LocalFileName;
                if (remote_size == 0)
                {
                    fdi.LocalFileStream = File.Create(localfile);
                    fdi.LocalFileStream.Close();
                    return;
                }

                EnterPassiveMode();
                if (fdi.DownloadedBytes > 0)
                {
                    // 尝试断点续传
                    if (!time.Equals(fdi.SavedLastModifiedTime) || remote_size != fdi.SavedSize)
                    {
                        fdi.Notify("远程文件在上一次暂停的传输之后已经被修改，从0开始下载");
                        fdi.DownloadedBytes = 0;
                    }
                    else
                    {
                        SendCommand("REST " + fdi.DownloadedBytes);
                        try
                        {
                            var message = ReceiveResponse(300, 350);
                            if (message == null) throw new FTPResponseException();
                        }
                        catch (FTPResponseException)
                        {
                            // 服务器可能不支持REST指令
                            fdi.Notify("服务器不支持断点续传，从0开始下载");
                            fdi.DownloadedBytes = 0;
                        }
                    }
                }

                fdi.SavedLastModifiedTime = time;
                fdi.SavedSize = remote_size;

                if (fdi.DownloadedBytes == 0 && fdi.LocalFileStream != null) 
                {
                    fdi.LocalFileStream.Seek(0, SeekOrigin.Begin);
                    fdi.LocalFileStream.SetLength(0);
                }
                else if (fdi.DownloadedBytes == 0)
                {
                    fdi.LocalFileStream = File.Create(localfile);
                }

                dataSocket.ReceiveBufferSize = 1 * 1024 * 1024;
                SendCommand("RETR " + fdi.RemoteFileName);
                // 等待服务器响应 125 Data connection already open 或 150 about to open data connection
                bool response226Ahead = false; // 是否与125,150一同收到了226消息
                var msgs = ReceiveResponse(125, 150, 226);
                if (msgs == null) throw new FTPResponseException("远程主机对于 RETR 命令没有预期的响应或超时");
                byte[] buf = new byte[1 * 1024 * 1024];
                while (dataSocket.Available > 0 || fdi.DownloadedBytes < fdi.SavedSize)
                {
                    token.ThrowIfCancellationRequested();
                    var length = dataSocket.Receive(buf);
                    if (dataSocket.Connected == false) throw new FTPResponseException("远程主机断开连接");
                    fdi.LocalFileStream.Write(buf, 0, length);
                    fdi.DownloadedBytes += length;
                }
                
                if (!response226Ahead) WaitResponse(226);

                dataSocket.Shutdown(SocketShutdown.Send);

                fdi.LocalFileStream.Close();
                dataSocket.Disconnect(true);

                if (fdi.DownloadedBytes != remote_size)
                {
                    throw new FTPResponseException("下载的字节数与远程文件大小不一致 " + localfile);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new FTPResponseException(ex.Message);
            }
        }

        public void UploadFile(FileUploadingInfo fui, CancellationToken token)
        {
            try
            {
                ChangeCurrentWorkingDirectory(fui.RemotePath);

                string localfile = fui.LocalPath + (fui.LocalPath.EndsWith("\\") ? "" : "\\") + fui.LocalFileName;
                string remotefile = fui.RemotePath + (fui.RemotePath.EndsWith("/") ? "" : "/") + fui.RemoteFileName;

                if (fui.UploadedBytes == 0)
                {
                    var size = GetFileSize(remotefile);
                    if (size != -1) throw new FTPResponseException("远程文件已存在");
                }
                else
                {
                    long size = -1;
                    int retryCount = 0;
                    // Note: Windows Server IIS FTP会锁住未完全上传的文件，解锁时机未知，需要重试等待
                    while (size == -1 && retryCount <= 3)
                    {
                        retryCount++;
                        Utils.WriteDebugInfo(connectionName, "恢复上传：正在尝试获取文件大小，尝试次数：" + retryCount);
                        size = GetFileSize(remotefile);
                        Thread.Sleep(500);
                    }
                    if (size == -1) throw new FTPResponseException("无法获取远程文件大小，上传程序无法确定REST位置，请稍后重试或重新上传。");
                    if (size != fui.UploadedBytes)
                    {
                        if (size < fui.UploadedBytes)
                        {
                            fui.Notify("Remote File Size小于UploadedBytes，将从Remote Size恢复下载，建议检查文件正确性。");
                            fui.UploadedBytes = size;
                        } else throw new FTPResponseException("Remote File Size大于UploadedBytes，文件很可能已被其他程序修改，请重新上传。");
                    }
                }
                SendCommand("CWD " + remotefile);
                Dictionary<int, string> response = null;
                try
                {
                    response = ReceiveResponse(250);
                } catch (FTPResponseException) { }
                if (response != null) throw new FTPResponseException("同名远程文件夹已存在");

                FileInfo fileInfo = new FileInfo(localfile);
                fui.FileSize = fileInfo.Length;

                EnterPassiveMode();
                if (fui.UploadedBytes > 0)
                {
                    fui.LocalFileStream.Seek(fui.UploadedBytes, SeekOrigin.Begin);
                    SendCommand("REST " + fui.UploadedBytes);
                    try
                    {
                        var message = ReceiveResponse(300, 350);
                        if (message == null) throw new FTPResponseException();
                    }
                    catch (FTPResponseException)
                    {
                        // 服务器可能不支持REST指令
                        fui.Notify("服务器不支持断点续传，从0开始上传");
                        fui.UploadedBytes = 0;
                    }
                }
                else
                {
                    fui.LocalFileStream = File.Open(localfile, FileMode.Open, FileAccess.Read, FileShare.Read);
                    SendCommand("ALLO " + fui.FileSize);
                    var rp = ReceiveRawResponse();
                }

                dataSocket.SendBufferSize = 1024 * 1024;
                SendCommand("STOR " + remotefile);
                // 等待服务器响应 125 Data connection already open 或 150 about to open data connection
                var msgs = ReceiveResponse(125, 150);
                if (msgs == null) throw new FTPResponseException("远程主机对于 STOR 命令没有预期的响应或超时");
                byte[] buf = new byte[1024 * 1024];
                while (fui.UploadedBytes < fui.FileSize)
                {
                    token.ThrowIfCancellationRequested();
                    int total = fui.LocalFileStream.Read(buf, 0, buf.Length);
                    var sent = dataSocket.Send(buf, 0, total, SocketFlags.None);
                    if (sent != total) throw new ApplicationException("sent!=total");
                    if (dataSocket.Connected == false) throw new FTPResponseException("远程主机断开连接");
                    fui.UploadedBytes += sent;
                }
                fui.LocalFileStream.Close();
                dataSocket.Shutdown(SocketShutdown.Send);
                dataSocket.Disconnect(true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new FTPResponseException(ex.Message);
            }
        }

        public long GetFileSize(string fullFileName)
        {
            try
            {
                SendCommand("SIZE " + fullFileName);
                var response = WaitResponse(213);
                return long.Parse(response.Substring(4, response.Length - 4)); // 去掉响应码
            } catch (SocketException ex)
            {
                throw new FTPResponseException(ex.Message);
            } catch (FormatException) { }
            catch (OverflowException) { }
            catch (ArgumentOutOfRangeException) { }
            catch (FTPResponseException) { } // 可能是目录
            return -1;
        }

        public string GetFileLastModifiedTime(string fullFileName)
        {
            try
            {
                SendCommand("MDTM " + fullFileName);
                var response = WaitResponse(213);
                return response.Substring(4, response.Length - 4); // 去掉响应码
            }
            catch (SocketException ex)
            {
                throw new FTPResponseException(ex.Message);
            }
            catch (ArgumentOutOfRangeException) { }
            catch (FTPResponseException) { } // 可能是目录
            return null;
        }

        public string GetCurrentWorkingDirectory()
        {
            try
            {
                SendCommand("PWD");
                var response = WaitResponse(257);
                // e.g. 257 "/" is the current directory
                try
                {
                    int lc = response.IndexOf('"');
                    int rc = response.IndexOf('"', lc + 1);
                    return response.Substring(lc + 1, rc - lc - 1);
                }
                catch (Exception)
                {
                    throw new FTPResponseException("Server 对 PWD 命令的响应数据有误");
                }
            }
            catch (SocketException ex)
            {
                throw new FTPResponseException(ex.Message);
            }
        }

        public void ChangeCurrentWorkingDirectory(string new_dir)
        {
            try
            {
                SendCommand("CWD " + new_dir);
                WaitResponse(250);
            }
            catch (SocketException ex)
            {
                throw new FTPResponseException(ex.Message);
            }
        }

        /// <summary>
        /// 发送一个NOOP(No operation)指令，用来保持连接
        /// </summary>
        public void SendNullCommand()
        {
            try
            {
                SendCommand("NOOP");
                ReceiveRawResponse();
            }
            catch (SocketException ex)
            {
                throw new FTPResponseException(ex.Message);
            }
        }

        private void SendCommand(string cmd)
        {
            var buf = Encoding.UTF8.GetBytes(cmd + "\r\n");
            cmdSocket.Send(buf);
            Utils.WriteDebugInfo(connectionName, Encoding.UTF8.GetString(buf));
        }

        /// <summary>
        /// 读取接收缓冲区，返回未经过处理的数据
        /// </summary>
        /// <returns></returns>
        private string ReceiveRawResponse()
        {
            var length = cmdSocket.Receive(buffer);
            var message = Encoding.UTF8.GetString(buffer, 0, length);
            while (!message.EndsWith("\r\n"))
            {
                length = cmdSocket.Receive(buffer);
                // 若 Socket 断开，Receive不会阻塞并直接返回0，故需判断以避免死循环
                if (cmdSocket.Connected == false) throw new FTPResponseException("远程主机断开连接");
                message += Encoding.UTF8.GetString(buffer, 0, length);
            }
            Utils.WriteDebugInfo(connectionName, message);
            return message;
        }

        /// <summary>
        /// 读取接收缓冲区数据，并返回指定的服务器响应消息。
        /// 如果暂无指定的响应消息，返回 null
        /// </summary>
        /// <param name="expected_response_code">希望得到的响应码</param>
        /// <returns>响应消息数组</returns>
        private Dictionary<int, string> ReceiveResponse(params int[] expected_response_code)
        {
            var msg = ReceiveRawResponse();
            Dictionary<int, string> responses = new Dictionary<int, string>();
            try
            {
                var msgs = msg.Split(new string[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var m in msgs)
                {
                    var response_code = int.Parse(m.Substring(0, 3));
                    if (((IList)expected_response_code).Contains(response_code)) responses[response_code] = m;
                    else
                    {
                        // 如果没有指明接收4、5开头的响应码，视作异常抛出
                        if (m.StartsWith("5")) { throw new FTPResponseException(m); }
                        else if (m.StartsWith("4")) { throw new FTPResponseException(m); }
                    }
                }
            }
            catch (FTPResponseException) { throw; }
            catch (SocketException) { throw; }
            catch (Exception) { return null; }
            if (responses.Count == 0) return null;
            else return responses;
        }

        /// <summary>
        /// 等待服务器响应指定的消息
        /// </summary>
        /// <param name="expected_response_code">希望得到的响应码</param>
        /// <returns>对应的响应消息</returns>
        private string WaitResponse(int expected_response_code)
        {
            var responses = ReceiveResponse(expected_response_code);
            if (responses == null) throw new FTPResponseException("连接方在一段时间后没有正确答复或连接的主机没有反应");
            return responses[expected_response_code];
        }

        private readonly byte[] buffer = new byte[4096];
    }

    class FileDownloadingInfo
    {
        public delegate void FTPDownloadingInfoHandler(FileDownloadingInfo source, params string[] messages);
        public event FTPDownloadingInfoHandler FTPDownloadingInfoNotifications;

        public void Close()
        {
            LocalFileStream?.Close();
        }

        public FileDownloadingInfo(ServerInfo server, string remotePath, string remoteFileName, string localPath, string localFileName)
        {
            IsFinished = false;
            RemotePath = remotePath; RemoteFileName = remoteFileName;
            LocalPath = localPath; LocalFileName = localFileName;
            serverInfo = server;
            DownloadedBytes = 0;
            SavedSize = -1;
        }

        public string RemotePath { set; get; }
        public string RemoteFileName { set; get; }
        public string LocalPath { set; get; }
        public string LocalFileName { set; get; }
        public long DownloadedBytes { set { downloadedBytes = value; Notify(); } get { return downloadedBytes; } }
        private long downloadedBytes;
        /// <summary>
        /// SavedLastModifiedTime: 调用REST指令前需要判断远程文件是否修改了，
        /// 该字段由DownloadFile函数自动设置
        /// </summary>
        public string SavedLastModifiedTime { set; get; }
        public long SavedSize { set; get; }
        /// <summary>
        /// 用以锁住文件，在全部下载完成前其他应用不能修改文件，由DownloadFile函数自动设置
        /// </summary>
        public FileStream LocalFileStream { set; get; }
        public void Notify(params string[] msgs) { FTPDownloadingInfoNotifications?.Invoke(this, msgs); }
        public bool IsFinished { set { isFinished = value; Notify(); } get { return isFinished; } }
        private bool isFinished;
        public object Tag { set; get; }
        public readonly ServerInfo serverInfo;

        override public string ToString()
        {
            return string.Format("RemotePath = {0} on server {4}, RemoteFileName = {1}, LocalPath = {2}, LocalFileName = {3}", RemotePath, RemoteFileName, LocalPath, LocalFileName, serverInfo.ServerIP);
        }
    }

    class FileUploadingInfo
    {
        public delegate void FTPUploadingInfoHandler(FileUploadingInfo source, params string[] messages);
        public event FTPUploadingInfoHandler FTPUploadingInfoNotifications;

        public void Close()
        {
            LocalFileStream?.Close();
        }

        public FileUploadingInfo(ServerInfo server, string localPath, string localFileName, string remotePath, string remoteFileName)
        {
            IsFinished = false;
            RemoteFileName = remoteFileName; RemotePath = remotePath;
            LocalFileName = localFileName; LocalPath = localPath;
            serverInfo = server;
            UploadedBytes = 0;
            FileSize = -1;
        }

        public string RemoteFileName { set; get; }
        public string RemotePath { set; get; }
        public string LocalFileName { set; get; }
        public string LocalPath { set; get; }
        public long UploadedBytes { set { uploadedBytes = value; Notify(); } get { return uploadedBytes; } }
        private long uploadedBytes;
        public long FileSize { get; set; }
        /// <summary>
        /// 用以锁住文件，在全部下载完成前其他应用不能修改文件，由UploadFile函数自动设置
        /// </summary>
        public FileStream LocalFileStream { set; get; }
        public void Notify(params string[] msgs) { FTPUploadingInfoNotifications?.Invoke(this, msgs); }
        public bool IsFinished { set { isFinished = value; Notify(); } get { return isFinished; } }
        private bool isFinished;
        public object Tag { set; get; }
        public readonly ServerInfo serverInfo;

        override public string ToString()
        {
            return string.Format("RemotePath = {0} on server {4}, RemoteFileName = {1}, LocalPath = {2}, LocalFileName = {3}", RemotePath, RemoteFileName, LocalPath, LocalFileName, serverInfo.ServerIP);
        }
    }

    class ServerInfo
    {
        public string ServerIP { get; set; }
        public int ServerPort { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }

    /// <summary>
    /// 用于终止后续的FTP命令，该异常应当在 FTPService 方法中被捕获并通知 UI
    /// 约定：收到该异常后，必须重新建立FTP连接(new FTPConnection)
    /// </summary>
    [Serializable]
    class FTPResponseException : ApplicationException
    {
        public FTPResponseException() { Recoverable = false; }
        public FTPResponseException(string response, bool recoverable = false) : base(response)
        {
            FTPResponse = response; Recoverable = recoverable;
        }

        public string FTPResponse { get; set; }
        public bool Recoverable { get; set; }

        [SecurityCritical]
        public override void GetObjectData(SerializationInfo info, StreamingContext context)
        {
            base.GetObjectData(info, context);
            info.AddValue("FTPResponse", FTPResponse);
        }
    }

    public delegate void FTPInfoHandler(string message);
}
