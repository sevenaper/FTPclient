using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FTPClient
{
    class LocalService
    {
        public delegate void LocalInfoHandler(string message);
        public event LocalInfoHandler LocalInfoNotifier;
        public Task<bool> ChangeWorkingDirectory(string newDirectory)
        {
            return Task.Run(() =>
            {
                try
                { 
                    CurrentLocalPath = newDirectory;
                    
                    return true;
                }
                catch (Exception)
                {
                    LocalInfoNotifier?.Invoke("无法切换到目录 " + newDirectory);
                }
                return false;
            });
        }
        public string CurrentLocalPath { get; set; }
        
    }
}
