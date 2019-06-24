using Microsoft.VisualBasic.FileIO;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace FTPClient
{
    /// <summary>
    /// 涉及到 本地文件服务 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {

        private LocalService localService = new LocalService();

        //TODO: CRITICAL! 需要重构

        private string currentLocalPath { get { return localService.CurrentLocalPath; }
            set { localService.CurrentLocalPath = value; }
        }

        private readonly BindingList<LocalFile> localFileList = new BindingList<LocalFile>();

        private void ReturnToParentDirLocal_Click(object sender, RoutedEventArgs e)
        {
            DirectoryInfo root = new DirectoryInfo(currentLocalPath);
            if (root.Parent != null)
            {
                TextLocalPath.Text = currentLocalPath = root.Parent.FullName;
                GetLocalFiles();
            }
        }

        private void RefreshLocal_Click(object sender, RoutedEventArgs e)
        {
            currentLocalPath = TextLocalPath.Text;
            GetLocalFiles();
        }

        private void OpenLocalFile_Click(object sender, RoutedEventArgs e)
        {
            if (ListViewLF.SelectedIndex == -1) return;
            if (localFileList[ListViewLF.SelectedIndex].IsDirectory)
            {
                TextLocalPath.Text = currentLocalPath = currentLocalPath + "\\" + localFileList[ListViewLF.SelectedIndex].Name;
                GetLocalFiles();
            }
            else
                System.Diagnostics.Process.Start(currentLocalPath + "\\" + localFileList[ListViewLF.SelectedIndex].Name);
        }

        private void DeleteLocalFile_Click(object sender, RoutedEventArgs e)
        {
            if (ListViewLF.SelectedIndex == -1) return;
            if (localFileList[ListViewLF.SelectedIndex].IsDirectory)
            {
                MessageBox.Show("本程序不支持回收本地文件夹！", "FTP Client", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                FileSystem.DeleteFile(currentLocalPath + "\\" + localFileList[ListViewLF.SelectedIndex].Name
                    , UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            GetLocalFiles();
        }

        private async void ChangeWorkingDirLocal_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                await localService.ChangeWorkingDirectory(TextLocalPath.Text);
                Dispatcher.Invoke(new Action(() =>
                {
                    RefreshLocalInfo();
                }));
            }
        }

        private void NewLocalDirectory_Click(object sender, RoutedEventArgs e)
        {
            // TODO:  ..
        }

        public class LocalFile
        {
            public bool IsDirectory { get; set; }
            public string Name { get; set; }
            public string Size { get; set; }
        }

        private void GetLocalFiles()
        {
            localFileList.Clear();
            DirectoryInfo root = new DirectoryInfo(TextLocalPath.Text);
            foreach (DirectoryInfo di in root.GetDirectories())
            {
                localFileList.Add(new LocalFile() { IsDirectory = true, Name = di.Name });
            }
            foreach (FileInfo fi in root.GetFiles())
            {
                localFileList.Add(new LocalFile() { IsDirectory = false, Name = fi.Name, Size = Utils.SizeToFriendlyString(fi.Length) });
            }
        }

        // Note: must be implemented to refresh local file list and current local path.
        private void RefreshLocalInfo()
        {
            GetLocalFiles();
        }
    }
}
