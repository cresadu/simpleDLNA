﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Timers;
using NMaier.SimpleDlna.Server;
using NMaier.SimpleDlna.Utilities;

namespace NMaier.SimpleDlna.FileMediaServer
{
  public sealed class FileServer : Logging, IMediaServer, IVolatileMediaServer, IDisposable
  {

    private readonly Timer changeTimer = new Timer(20000);
    private Comparers.IItemComparer comparer = new Comparers.TitleComparer();
    private bool descending = false;
    private readonly DirectoryInfo directory;
    private readonly string friendlyName;
    private static readonly Random idGen = new Random();
    private Dictionary<string, IMediaItem> ids = new Dictionary<string, IMediaItem>();
    private Dictionary<string, string> paths = new Dictionary<string, string>();
    private IMediaFolder root;
    private Files.FileStore store = null;
    private readonly List<Views.IView> transformations = new List<Views.IView>();
    private MediaTypes types;
    private readonly Guid uuid = Guid.NewGuid();
    private readonly FileSystemWatcher watcher;



    public FileServer(MediaTypes types, DirectoryInfo directory)
    {
      this.types = types;
      this.directory = directory;
      friendlyName = string.Format("{0} ({1})", directory.Name, directory.Parent.FullName);
      watcher = new FileSystemWatcher(directory.FullName);
    }



    public bool DescendingOrder
    {
      get { return descending; }
      set { descending = value; }
    }

    public string FriendlyName
    {
      get { return friendlyName; }
    }

    public IMediaFolder Root
    {
      get { return root; }
    }

    public Guid Uuid
    {
      get { return uuid; }
    }




    public event EventHandler Changed;
    private Task thumberTask;




    public void AddView(string name)
    {
      transformations.Add(ViewRepository.Lookup(name));
    }

    public IMediaItem GetItem(string id)
    {
      return ids[id];
    }

    public void Load()
    {
      if (types == MediaTypes.AUDIO && transformations.Count == 0) {
        AddView("music");
      }
      DoRoot();

      changeTimer.AutoReset = false;
      changeTimer.Elapsed += RescanTimer;

      watcher.IncludeSubdirectories = true;
      watcher.Created += new FileSystemEventHandler(OnChanged);
      watcher.Deleted += new FileSystemEventHandler(OnChanged);
      watcher.Renamed += new RenamedEventHandler(OnRenamed);
      watcher.EnableRaisingEvents = true;
    }

    public void SetCacheFile(FileInfo info)
    {
      store = new Files.FileStore(info);
    }

    public void SetOrder(string order)
    {
      comparer = ComparerRepository.Lookup(order);
    }

    private IFileServerMediaItem CreateRoot(string ID, MediaTypes acceptedTypes, DirectoryInfo rootDirectory)
    {
      var rv = new Folders.PlainRootFolder(ID, this, acceptedTypes, rootDirectory);
      foreach (var t in transformations) {
        t.Transform(this, rv);
      }
      rv.Cleanup();
      rv.Sort(comparer, descending);
      return rv;
    }

    private void DoRoot()
    {
      // Collect some garbage
      lock (ids) {
        lock (paths) {
#if ENABLE_SAMSUNG
          // Remove specialized (Samsung) views, to avoid dupes
          ids.Remove("I");
          ids.Remove("A");
          ids.Remove("V");
#endif

          var newPaths = new Dictionary<string, string>();
          var newIds = new Dictionary<string, IMediaItem>();
          foreach (var i in ids) {
            if (i.Value is Files.BaseFile && !(i.Value as Files.BaseFile).Item.Exists) {
              continue;
            }
            try {
              newIds.Add(i.Key, i.Value);
              var path = (i.Value as IFileServerMediaItem).Path;
              newPaths.Add(path, i.Key);
            }
            catch (Exception ex) {
              Error(i.Key);
              Error((i.Value as IFileServerMediaItem).Path);
              Error(ex);
              throw;
            }
          }
          paths = newPaths;
          ids = newIds;
        }
      }

      lock (ids) {
        ids["0"] = root = CreateRoot("0", types, directory) as IMediaFolder;
#if ENABLE_SAMSUNG
        var typeView = CreateRoot("I", types & MediaTypes.IMAGE, directory);
        typeView.Parent = root as Folders.BaseFolder;
        ids["I"] = typeView;
        typeView = CreateRoot("A", types & MediaTypes.AUDIO, directory);
        typeView.Parent = root as Folders.BaseFolder;
        ids["A"] = typeView;
        typeView = CreateRoot("V", types & MediaTypes.VIDEO, directory);
        typeView.Parent = root as Folders.BaseFolder;
        ids["V"] = typeView;
#endif
      }
#if DUMP_TREE
      using (var s = new FileStream("tree.dump", FileMode.Create, FileAccess.Write)) {
        using (var w = new StreamWriter(s)) {
          DumpTree(w, root);
        }
      }
#endif
      if (store != null && thumberTask == null) {
        var files = (from i in ids.Values
                     let f = (i as Files.BaseFile)
                     where f != null
                     select new WeakReference(f)).ToList();
        thumberTask = Task.Factory.StartNew(() =>
        {
          try {
            foreach (var i in files) {
              try {
                var item = (i.Target as Files.BaseFile);
                if (item == null) {
                  continue;
                }
                if (store.HasCover(item)) {
                  continue;
                }
                item.LoadCover();
                using (var k = item.Cover.Content) {
                  k.ReadByte();
                }
              }
              catch (Exception ex) {
                Debug("Failed to thumb", ex);
              }
            }
          }
          catch (Exception ex) {
            Error(ex);
          }
          finally {
            thumberTask = null;
          }
        }, TaskCreationOptions.LongRunning | TaskCreationOptions.AttachedToParent);
      }
    }
#if DUMP_TREE
    private void DumpTree(StreamWriter w, IMediaFolder folder, string prefix = "/")
    {
      foreach (IMediaFolder f in folder.ChildFolders) {
        w.WriteLine("{0} {1} - {2}", prefix, f.Title, f.GetType().ToString());
        DumpTree(w, f, prefix + f.Title + "/");
      }
      foreach (IMediaResource r in folder.ChildItems) {
        w.WriteLine("{0} {1} - {2}", prefix, r.Title, r.GetType().ToString());
      }
    }
#endif

    private void OnChanged(Object source, FileSystemEventArgs e)
    {
      if (store != null && e.FullPath.ToLower() == store.StoreFile.FullName.ToLower()) {
        return;
      }
      DebugFormat("File System changed: {0}", e.FullPath);
      changeTimer.Enabled = true;
    }

    private void OnRenamed(Object source, RenamedEventArgs e)
    {
      DebugFormat("File System changed (rename): {0}", directory.FullName);
      changeTimer.Enabled = true;
    }

    private void Rescan()
    {
      lock (this) {
        try {
          InfoFormat("Rescanning...");
          DoRoot();
          InfoFormat("Done rescanning...");

          if (Changed != null) {
            InfoFormat("Notifying...");
            Changed(this, null);
          }
        }
        catch (Exception ex) {
          Error(ex);
        }
      }
    }

    private void RescanTimer(object sender, ElapsedEventArgs e)
    {
      Rescan();
    }

    internal Files.Cover GetCover(Files.BaseFile file)
    {
      if (store != null) {
        return store.MaybeGetCover(file);
      }
      return null;
    }

    internal Files.BaseFile GetFile(Folders.BaseFolder aParent, FileInfo aFile)
    {
      string key;
      if (paths.TryGetValue(aFile.FullName, out key)) {
        IMediaItem item;
        if (ids.TryGetValue(key, out item) && item is Files.BaseFile) {
          var ev = item as Files.BaseFile;
          if (ev.Parent is Folders.BaseFolder) {
            (ev.Parent as Folders.BaseFolder).ReleaseItem(ev);
          }
          if (ev.InfoDate == aFile.LastWriteTimeUtc && ev.InfoSize == aFile.Length) {
            ev.Parent = aParent;
            return ev;
          }
        }
      }

      var ext = new Regex(@"[^\w\d]+", RegexOptions.Compiled).Replace(aFile.Extension.ToLower().Substring(1), "");
      var type = DlnaMaps.Ext2Dlna[ext];
      var mediaType = DlnaMaps.Ext2Media[ext];

      if (store != null) {
        var sv = store.MaybeGetFile(aParent, aFile, type);
        if (sv != null) {
          return sv;
        }
      }

      return Files.BaseFile.GetFile(aParent, aFile, type, mediaType);
    }

    internal void RegisterPath(IFileServerMediaItem item)
    {
      var path = item.Path;
      string id;
      if (!paths.ContainsKey(path)) {
        while (ids.ContainsKey(id = idGen.Next(1000, int.MaxValue).ToString()))
          ;
        paths[path] = id;
      }
      else {
        id = paths[path];
      }
      ids[id] = item;
      item.Id = id;
    }

    internal void UpdateFileCache(Files.BaseFile aFile)
    {
      if (store != null) {
        store.MaybeStoreFile(aFile);
      }
    }

    public void Dispose()
    {
      if (watcher != null) {
        watcher.Dispose();
      }
      if (changeTimer != null) {
        changeTimer.Dispose();
      }
      if (store != null) {
        store.Dispose();
      }
    }
  }
}
