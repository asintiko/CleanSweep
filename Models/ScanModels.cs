using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CleanSweep.Models
{
    public class FileItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        public string FullPath { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Directory { get; set; } = "";
        public long Size { get; set; }
        public string SizeHuman { get; set; } = "";
        public string Modified { get; set; } = "";
        public string Extension { get; set; } = "";
        public string Hash { get; set; } = "";
        public int GroupId { get; set; }
        public bool IsOriginal { get; set; }
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class DuplicateGroup
    {
        public int Id { get; set; }
        public List<FileItem> Files { get; set; } = new();
        public long TotalSize { get; set; }
        public string TotalSizeHuman { get; set; } = "";
        public string Similarity { get; set; } = "";
    }

    public class JunkCategory : INotifyPropertyChanged
    {
        private bool _isSelected = true;
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string IconKey { get; set; } = "";
        public int FileCount { get; set; }
        public long TotalSize { get; set; }
        public string TotalSizeHuman { get; set; } = "";
        public List<string> FilePaths { get; set; } = new();
        public bool IsSelected
        {
            get => _isSelected;
            set { _isSelected = value; OnPropertyChanged(); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? n = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }

    public class FolderSizeItem
    {
        public string FullPath     { get; set; } = "";
        public string Name         { get; set; } = "";
        public long   Size         { get; set; }
        public string SizeHuman    { get; set; } = "";
        public int    FileCount    { get; set; }
        public double RelativeSize { get; set; } // 0.0–1.0 for bar width
    }

    public class EmptyFolderItem
    {
        private bool _sel = true;
        public string FullPath  { get; set; } = "";
        public string Name      { get; set; } = "";
        public string Parent    { get; set; } = "";
        public bool IsSelected  { get => _sel; set => _sel = value; }
    }

    public class StartupItem
    {
        private bool _en = true;
        public string Name        { get; set; } = "";
        public string Command     { get; set; } = "";
        public string Source      { get; set; } = "";
        public string RegistryKey { get; set; } = "";
        public bool IsSelected    { get; set; }
        public bool IsEnabled     { get => _en; set => _en = value; }
    }

    public class ShortcutItem
    {
        public string ShortcutPath { get; set; } = "";
        public string ShortcutName { get; set; } = "";
        public string TargetPath   { get; set; } = "";
        public string Location     { get; set; } = "";
        public bool   IsSelected   { get; set; } = true;
    }
}
