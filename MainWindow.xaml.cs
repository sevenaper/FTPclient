using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using Microsoft.VisualBasic.FileIO;

namespace FTPClient
{
    /// <summary>
    /// 涉及到 上传按键、下载按键、拖拽事件 的交互逻辑
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            TextLocalPath.Text = currentLocalPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            ListViewLF.ItemsSource = localFileList;
            ListViewRF.ItemsSource = remoteFileList;
            ListViewStatus.ItemsSource = taskInfoList;
            GetLocalFiles();
        }

        private void ListViewLF_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // 判断鼠标是否在ListViewItem上双击
            if (Utils.GetContainerAtPoint<ListViewItem>(ListViewLF, e.GetPosition(ListViewLF)) == null) return;
            UploadLocalFile_Click(null, null);
        }

        private void ListViewRF_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            // 判断鼠标是否在ListViewItem上双击
            if (Utils.GetContainerAtPoint<ListViewItem>(ListViewRF, e.GetPosition(ListViewRF)) == null) return;
            DownloadRemoteFile_Click(null, null);
        }

        private void UploadLocalFile_Click(object sender, RoutedEventArgs e)
        {
            if (ListViewLF.SelectedIndex == -1) return;
            if (localFileList[ListViewLF.SelectedIndex].IsDirectory)
            {
                TextLocalPath.Text = currentLocalPath = currentLocalPath + "\\" + localFileList[ListViewLF.SelectedIndex].Name;
                GetLocalFiles();
            }
            else
            {
                var name = localFileList[ListViewLF.SelectedIndex].Name;
                UploadLocalFile(name, name);
            }
        }

        private async void DownloadRemoteFile_Click(object sender, RoutedEventArgs e)
        {
            if (ListViewRF.SelectedIndex == -1) return;
            if (connectionStatus != ConnectionStatus.Connected) return;
            if (remoteFileList[ListViewRF.SelectedIndex].IsDirectory)
            {
                await ftpService.ChangeWorkingDirectory(remoteFileList[ListViewRF.SelectedIndex].Name);
                RefreshRemoteInfo();
            }
            else
            {
                var name = remoteFileList[ListViewRF.SelectedIndex].Name;
                DownloadRemoteFile(name, name);
            }
        }

        private void DownloadRemoteFileTo_Click(object sender, RoutedEventArgs e)
        {
            // TODO: 打开文件保存框
        }


        #region No-border UI related

        private void MinimizeWindow(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void MaximizeWindow(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void CloseWindow(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void TitleBar_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }


        #endregion


    }
}
