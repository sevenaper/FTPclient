using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace FTPClient
{
    partial class FTPConnection
    {

        public void ChangeFileOrDirName(string oldFileName, string newFileName)
        {
            try
            {
                SendCommand("RNFR " + oldFileName);
                WaitResponse(350);
                SendCommand("RNTO " + newFileName);
                WaitResponse(250);
            }
            catch (SocketException ex)
            {
                throw new FTPResponseException(ex.Message);
            }
        }

        public void MoveFileOrDir(string fileName, string newPath)
        {
            ChangeFileOrDirName(fileName, newPath + "/" + fileName);
        }

        public void CopyFileOrDirOnServer(string fileName, string newPath)
        {
            try
            {
                SendCommand("CPFR " + "/");
                var msg = ReceiveRawResponse();
            }
            catch (SocketException ex)
            {
                throw new FTPResponseException(ex.Message);
            }
        }
        public void DeleteFile(string fileName)
        {
            try
            {
                SendCommand("DELE " + fileName);
                WaitResponse(250);
                //var msg = ReceiveRawResponse();

            }
            catch (SocketException ex)
            {
                throw new FTPResponseException(ex.Message);
            }
        }
        public void DeleteDir(string dirName)
        {
            try
            {
                SendCommand("RMD " + dirName);
                WaitResponse(250);
                //var msg = ReceiveRawResponse();
            }
            catch (SocketException ex)
            {
                throw new FTPResponseException(ex.Message);
            }
        }
        
        public void MakeDirectory(string dictName)
        {
            try
            {
                SendCommand("MKD " + dictName);
                var msg = ReceiveRawResponse();
            }
            catch (SocketException ex)
            {
                throw new FTPResponseException(ex.Message);
            }
        }

       
    }

    partial class FTPService
    {
        
        public Task<bool> DeleteDirWithFiles(string name)
        {
            return Task.Run(() =>
            {
                try
                {
                    FTPConnection ftpConnection = CreateConnection(Server);
                    var result = DeleteDir(name, ftpConnection);
                    ftpConnection.Close();
                    return result;
                    
                }
                catch (FTPResponseException ex)
                {
                    FTPErrorNotifications?.Invoke(ex);
                }
                return false;
            });
        }

        private bool DeleteDir(string name, FTPConnection ftpConnection)
        {
            try
            {
                ftpConnection.ChangeCurrentWorkingDirectory(name);

                string result = ftpConnection.GetFileList();
                var list = ParseFileList(ftpConnection, result);
                foreach (var file in list)
                {
                    if (!file.IsDirectory)
                    {
                        ftpConnection.DeleteFile(file.Name);
                    }
                    else
                    {
                        var ret = DeleteDir(name + "/" + file.Name, ftpConnection);
                    }
                }

                ftpConnection.DeleteDir(name);

                return true;
            }
            catch (FTPResponseException ex)
            {
                FTPErrorNotifications?.Invoke(ex);
            }
            return false;
        }

        public Task<bool> DeleteFile(string name)
        {
            return Task.Run(() =>
            {
                try
                {
                    lock (persistentConnectionLock)
                    {
                        persistentConnection.DeleteFile(name);
                    }
                    return true;
                }
                catch (FTPResponseException)
                {
                    FTPInfoNotifier?.Invoke("无法删除文件 " + name);
                }
                return false;
            });
        }

        public Task<bool> RenameDirOrFile(string oldname,string newname)
        {
            return Task.Run(() =>
            {
                try
                {
                    lock (persistentConnectionLock)
                    {
                        persistentConnection.ChangeFileOrDirName(oldname,newname);
                    }
                    return true;
                }
                catch (FTPResponseException)
                {
                    FTPInfoNotifier?.Invoke("无法重命名文件 " + oldname);
                }
                return false;
            });
        }

        public Task<bool> MakeNewDir(string name)
        {
            return Task.Run(() =>
            {
                try
                {
                    lock (persistentConnectionLock)
                    {
                        persistentConnection.MakeDirectory(name);
                    }
                    return true;
                }
                catch (FTPResponseException)
                {
                    FTPInfoNotifier?.Invoke("无法新建文件夹 " + name);
                }
                return false;
            });
        }
    }
}
