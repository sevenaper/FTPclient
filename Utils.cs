using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FTPClient
{
    class Utils
    {
        public static string SizeToFriendlyString(long size)
        {
            if (size < 1024) return size.ToString() + " Bytes";
            else if (size >= 1024 && size < 1024 * 1024) return (size / 1024).ToString() + " KB";
            else if (size >= 1024 * 1024 && size < 1024 * 1024 * 1024) return (size / 1024 / 1024).ToString() + " MB";
            else return (size / 1024 / 1024 / 1024).ToString() + " GB";
        }

        public static string SpeedToFriendlyString(long speedinseconds)
        {
            return SizeToFriendlyString(speedinseconds) + "/s";
        }

        public static string TimeToFriendlyString(long timeinseconds)
        {
            if (timeinseconds < 0) return "未知";
            if (timeinseconds <= 60) return timeinseconds.ToString() + " 秒";
            return (timeinseconds / 60).ToString() + " 分" + (timeinseconds % 60) + " 秒";
        }

        public static void WriteDebugInfo(string uniqueID, string info)
        {
           // Debug.WriteLine(info);
            LogWindow logWindow = null;
            Application.Current.Dispatcher?.BeginInvoke(new Action(() => {
                foreach(Window window in Application.Current.Windows)
                {
                    if(window.GetType() == typeof(LogWindow))
                    {
                        logWindow = window as LogWindow;
                        break;
                    }
                }
                logWindow.LogMsg = logWindow.LogMsg + "连接 " + uniqueID + ": " + info + (info.EndsWith("\r\n") ? "" : "\r\n");
            }));
        }

        public static object GetObjectAtPoint<ItemContainer>(ItemsControl control, Point p) where ItemContainer : DependencyObject
        {
            // ItemContainer - can be ListViewItem, or TreeViewItem and so on(depends on control)
            ItemContainer obj = GetContainerAtPoint<ItemContainer>(control, p);
            if (obj == null) return null;
            return control.ItemContainerGenerator.ItemFromContainer(obj);
        }

        public static ItemContainer GetContainerAtPoint<ItemContainer>(ItemsControl control, Point p) where ItemContainer : DependencyObject
        {
            HitTestResult result = VisualTreeHelper.HitTest(control, p);
            DependencyObject obj = result.VisualHit;
            if (obj == null) return null;

            while (VisualTreeHelper.GetParent(obj) != null && !(obj is ItemContainer))
            {
                obj = VisualTreeHelper.GetParent(obj);
            }

            // Will return null if not found
            return obj as ItemContainer;
        }

        public static T FindAnchestor<T>(DependencyObject current) where T : DependencyObject
        {
            do
            {
                if (current is T)
                {
                    return (T)current;
                }
                current = VisualTreeHelper.GetParent(current);
            }
            while (current != null);
            return null;
        }
    }

}
