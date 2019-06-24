using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Navigation;

namespace FTPClient
{
    public partial class MainWindow : Window
    {

        private void WindowRendered(object sender, EventArgs e)
        {
            logWindow.Owner = this;
        }

        private void DeleteRemoteFileOrDir_Click(object sender, RoutedEventArgs e)
        {
            if (connectionStatus != ConnectionStatus.Connected) return;
            if (ListViewRF.SelectedIndex == -1) return;
            var item = remoteFileList[ListViewRF.SelectedIndex];
            if (item.IsDirectory)
            {
                ftpService.DeleteDirWithFiles(ftpService.CurrentRemotePath + item.Name);
            }
            else
            {
                ftpService.DeleteFile(item.Name);
            }
            remoteFileList.Remove(item);


        }

        private void NewRemoteDirectory_Click(object sender, RoutedEventArgs e)
        {
            if (connectionStatus != ConnectionStatus.Connected) return;
            RemoteFile rf = new RemoteFile();
            rf.IsDirectory = true;
            RemoteFileInfoUI remoteDir = new RemoteFileInfoUI(rf);  
            if (ListViewRF.SelectedIndex == 0)
            {
                remoteFileList.Insert(0,remoteDir);
                ListViewRF.SelectedIndex = 0;
            }
            else
            {
                remoteFileList.Insert(ListViewRF.SelectedIndex + 1, remoteDir);
                ListViewRF.SelectedIndex ++;
            }
            
            if (!remoteDir.IsRenaming)
            {
                remoteDir.IsRenaming = true;
            }
            ListViewRF.UpdateLayout();
            var lvi = ListViewRF.ItemContainerGenerator.ContainerFromIndex(ListViewRF.SelectedIndex);
            TextBox textBox = FindVisualChild<TextBox>(lvi);
            TextBlock textBlock = FindVisualChild<TextBlock>(lvi);
            textBox.LostFocus += async (object lsender, RoutedEventArgs le) => {

                remoteDir.IsRenaming = false;
                bool result = await ftpService.MakeNewDir(textBox.Text);
                if (result == true) Dispatcher.Invoke(() => { remoteDir.Name = textBox.Text; });


            };
            textBox.Text = textBlock.Text;
            textBox.Focus();
        }

        private void RenameRemoteFileOrDir_Click(object sender, RoutedEventArgs e)
        {
            if (connectionStatus != ConnectionStatus.Connected) return;
            if (ListViewRF.SelectedIndex == -1) return;
            var item = remoteFileList[ListViewRF.SelectedIndex];
            var oldname = item.Name;
            if (!item.IsRenaming)
            {
                item.IsRenaming = true;
            }
            var lvi = ListViewRF.ItemContainerGenerator.ContainerFromIndex(ListViewRF.SelectedIndex);
            TextBox textBox = FindVisualChild<TextBox>(lvi);
            TextBlock textBlock = FindVisualChild<TextBlock>(lvi);
            textBox.LostFocus += async (object lsender, RoutedEventArgs le)=> {
                var newname = textBox.Text;
                item.IsRenaming = false;
                if (newname.IndexOf('/') != -1 || newname.IndexOf('\\') != -1)
                {
                    DefaultFTPInfoHandler("文件名有误。");
                    return;
                }
                bool result = await  ftpService.RenameDirOrFile(oldname, newname);
                if (result == true) Dispatcher.Invoke(() => { item.Name = newname; });
            };
            textBox.Text = textBlock.Text;
            textBox.Focus();
        }


        public readonly LogWindow logWindow = new LogWindow();

        private void LogShow_Click(object sender, RoutedEventArgs e)
        {
            logWindow.Show();
        }

        private void RemoteFile_Drop(object sender, DragEventArgs e)
        {
            if (connectionStatus != ConnectionStatus.Connected) return;

            RemoteFileInfoUI item = Utils.GetObjectAtPoint<ListViewItem>(ListViewRF, e.GetPosition(ListViewRF)) as RemoteFileInfoUI;
            if (e.Data.GetFormats()[0] == "RemoteFileInfoUI")//应用程序的远程列表
            {

            }
            else if(e.Data.GetFormats()[0] == "Shell IDList Array")//explorer拖入应用程序
            {
                string[] fileList = e.Data.GetData(DataFormats.FileDrop) as string[];
                string filename = Path.GetFileName(fileList[0]);
                if (item == null || !item.IsDirectory)
                {

                    UploadLocalFile(filename, filename);

                }
                else
                {
                    UploadLocalFile(filename, item.Name + @"\" + filename);
                }
            }
            else  // 从左侧localfile_list中拖入
            {
                LocalFile localFile = e.Data.GetData("LocalFile") as LocalFile;
                if (item == null || !item.IsDirectory)
                {

                    UploadLocalFile(localFile.Name, localFile.Name);

                }
                else
                {
                    UploadLocalFile(localFile.Name, item.Name + @"\" + localFile.Name);
                }
            }
        }

        private Point rStartPoint;

        private void RemoteFile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (connectionStatus != ConnectionStatus.Connected) return;
            rStartPoint = e.GetPosition(null);
        }

        private void RemoteFile_MouseMove(object sender, MouseEventArgs e)
        {
            if (connectionStatus != ConnectionStatus.Connected) return;
            Point mousePos = e.GetPosition(null);
            Vector diff = rStartPoint - mousePos;

            if (e.LeftButton == MouseButtonState.Pressed && (
                Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance))
            {
                // Get the dragged ListViewItem
                ListView listView = sender as ListView;
                ListViewItem listViewItem = Utils.FindAnchestor<ListViewItem>((DependencyObject)e.OriginalSource);
                if (listViewItem == null) return;
                // Find the data behind the ListViewItem
                RemoteFileInfoUI info = (RemoteFileInfoUI)listView.ItemContainerGenerator.ItemFromContainer(listViewItem);

                // Initialize the drag & drop operation
                DataObject dragData = new DataObject("RemoteFileInfoUI", info);
                
                DragDrop.DoDragDrop(listViewItem, dragData, DragDropEffects.Move);
            }
        }

        private void LocalFile_Drop(object sender, DragEventArgs e)
        {


            LocalFile item = Utils.GetObjectAtPoint<ListViewItem>(ListViewLF, e.GetPosition(ListViewLF)) as LocalFile;

            if (e.Data.GetFormats()[0] == "RemoteFileInfoUI")
            {
                RemoteFileInfoUI remoteFile = e.Data.GetData("RemoteFileInfoUI") as RemoteFileInfoUI;
                string filename = remoteFile.Name;
                if (item == null || !item.IsDirectory)
                {
                    DownloadRemoteFile(filename, filename);
                }
                else
                {
                    DownloadRemoteFile(filename, item.Name + @"\" + filename);
                }
            }
            else
            {
                string[] fileList = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (fileList == null) return;
                string filename = Path.GetFileName(fileList[0]);

                if (item == null || !item.IsDirectory)
                {
                    File.Copy(fileList[0], localService.CurrentLocalPath + @"\" + filename);

                }
                else
                {
                    File.Copy(fileList[0], localService.CurrentLocalPath + @"\" + item.Name + @"\" + filename);
                }
                GetLocalFiles();

            }


        }

        private void LocalFile_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            rStartPoint = e.GetPosition(null);
        }

        private void LocalFile_MouseMove(object sender, MouseEventArgs e)
        {
            Point mousePos = e.GetPosition(null);
            Vector diff = rStartPoint - mousePos;

            if (e.LeftButton == MouseButtonState.Pressed && (
                Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance))
            {
                // Get the dragged ListViewItem
                ListView listView = sender as ListView;
                ListViewItem listViewItem = Utils.FindAnchestor<ListViewItem>((DependencyObject)e.OriginalSource);
                if (listViewItem == null) return;
                // Find the data behind the ListViewItem
                LocalFile info = (LocalFile)listView.ItemContainerGenerator.ItemFromContainer(listViewItem);
                //RemoteFileInfoUI info = (RemoteFileInfoUI)listView.ItemContainerGenerator.ItemFromContainer(listViewItem);

                // Initialize the drag & drop operation
                DataObject dragData = new DataObject("LocalFile", info);
                DragDrop.DoDragDrop(listViewItem, dragData, DragDropEffects.Move);
            }
        }

        private void ListViewRF_Drop(object sender, DragEventArgs e)
        {
            if (connectionStatus != ConnectionStatus.Connected) return;

            

            if (ListViewRF.SelectedIndex == -1 || (!remoteFileList[ListViewRF.SelectedIndex].IsDirectory))
            {
                string[] fileList = e.Data.GetData(DataFormats.FileDrop) as string[];
                foreach (string file in fileList)
                {
                    UploadLocalFile(Path.GetDirectoryName(file), Path.GetFileName(file), ftpService.CurrentRemotePath, Path.GetFileName(file));
                }
            }
            else
            {
                
                string[] fileList = e.Data.GetData(DataFormats.FileDrop) as string[];
                string filename = Path.GetFileName(fileList[0]);
                UploadLocalFile(filename, remoteFileList[ListViewRF.SelectedIndex].Name + @"\" +filename);
            }
        }

       


        private ChildType FindVisualChild<ChildType>(DependencyObject obj) where ChildType : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(obj); i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(obj, i);
                if (child != null && child is ChildType)
                {
                    return child as ChildType;
                }
                else
                {
                    ChildType childOfChildren = FindVisualChild<ChildType>(child);
                    if (childOfChildren != null)
                    {
                        return childOfChildren;
                    }
                }
            }
            return null;
        }

        class LocalFileInfoUI
        {
            
        }

    }
    public class ScrollViewerExtensions
    {
        public static readonly DependencyProperty AlwaysScrollToEndProperty = DependencyProperty.RegisterAttached("AlwaysScrollToEnd", typeof(bool), typeof(ScrollViewerExtensions), new PropertyMetadata(false, AlwaysScrollToEndChanged));
        private static bool _autoScroll;


        private static void AlwaysScrollToEndChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            ScrollViewer scroll = sender as ScrollViewer;
            if (scroll != null)
            {
                bool alwaysScrollToEnd = (e.NewValue != null) && (bool)e.NewValue;
                if (alwaysScrollToEnd)
                {
                    scroll.ScrollToEnd();
                    scroll.ScrollChanged += ScrollChanged;
                    // scroll.SizeChanged += Scroll_SizeChanged;
                }
                else { scroll.ScrollChanged -= ScrollChanged; /*scroll.ScrollChanged -= ScrollChanged; */}
            }
            else { throw new InvalidOperationException("The attached AlwaysScrollToEnd property can only be applied to ScrollViewer instances."); }
        }


        public static bool GetAlwaysScrollToEnd(ScrollViewer scroll)
        {
            if (scroll == null) { throw new ArgumentNullException("scroll"); }
            return (bool)scroll.GetValue(AlwaysScrollToEndProperty);
        }


        public static void SetAlwaysScrollToEnd(ScrollViewer scroll, bool alwaysScrollToEnd)
        {
            if (scroll == null) { throw new ArgumentNullException("scroll"); }
            scroll.SetValue(AlwaysScrollToEndProperty, alwaysScrollToEnd);
        }


        private static void ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            ScrollViewer scroll = sender as ScrollViewer;
            if (scroll == null) { throw new InvalidOperationException("The attached AlwaysScrollToEnd property can only be applied to ScrollViewer instances."); }


            if (e.ExtentHeightChange == 0) { _autoScroll = scroll.VerticalOffset == scroll.ScrollableHeight; }
            if (_autoScroll && e.ExtentHeightChange != 0) { scroll.ScrollToVerticalOffset(scroll.ExtentHeight); }
        }
    }

}
