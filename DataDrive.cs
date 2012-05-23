﻿using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Diagnostics;

namespace disParity
{

  public class DataDrive
  {

    private string root;
    private string metaFileName;
    private FileRecord[] oldFiles;  // list of files previously protected on this drive, as loaded from files.dat
    private List<FileRecord> seenFiles; // list of current files on the drive as seen this scan
    private List<FileRecord> newFiles; // the *new* master file list as it is being updated this session by the app
    private MD5 hash;

    const UInt32 META_FILE_VERSION = 2;

    public DataDrive(string root, string metaFileName)
    {
      this.root = root;
      this.metaFileName = metaFileName;

      if (File.Exists(metaFileName))
        LoadFileData();

      hash = MD5.Create();
    }

    public string Root { get { return root; } }

    public UInt32 MaxBlock { get; private set; }

    /// <summary>
    /// Clears all state of the DataDrive, resetting to empty (deletes on-disk
    /// meta data as well.)
    /// </summary>
    public void Clear()
    {
      oldFiles = null;
      seenFiles = null;
      MaxBlock = 0;
      if (File.Exists(metaFileName))
        File.Delete(metaFileName);
    }

    /// <summary>
    /// Scans the drive from 'root' down, generating the current list of files on the drive.
    /// Files that should not be protected (e.g. Hidden files) are not included.
    /// </summary>
    public void Scan()
    {
      seenFiles = new List<FileRecord>();
      LogFile.Log("Scanning {0}...", root);
      Scan(new DirectoryInfo(root));
      long totalSize = 0;
      foreach (FileRecord f in seenFiles)
        totalSize += f.Length;
      LogFile.Log("Found {0} file{1} ({2} total)", seenFiles.Count, 
        seenFiles.Count == 1 ? "" : "s", Utils.SmartSize(totalSize));
    }

    private void Scan(DirectoryInfo dir)
    {
      DirectoryInfo[] subDirs;
      try {
        subDirs = dir.GetDirectories();
      }
      catch (Exception e) {
        LogFile.Log("Warning: Could not enumerate subdirectories of {0}: {1}", dir.FullName, e.Message);
        return;
      }
      FileInfo[] fileInfos;
      try {
        fileInfos = dir.GetFiles();
      }
      catch (Exception e) {
        LogFile.Log("Warning: Could not enumerate files in {0}: {1}", dir.FullName, e.Message);
        return;
      }
      foreach (DirectoryInfo d in subDirs) {
        if (IgnoreHidden && (d.Attributes & FileAttributes.Hidden) != 0)
          continue;
        if ((d.Attributes & FileAttributes.System) != 0)
          continue;
        Scan(d);
      }
      string relativePath = Utils.StripRoot(root, dir.FullName);
      foreach (FileInfo f in fileInfos) {
        if (f.Attributes == (FileAttributes)(-1))
          continue;
        if (IgnoreHidden && (f.Attributes & FileAttributes.Hidden) != 0)
          continue;
        if ((f.Attributes & FileAttributes.System) != 0)
          continue;
        seenFiles.Add(new FileRecord(f, relativePath, this));
      }
    }

    private List<FileRecord> adds;
    public List<FileRecord> Adds { get { return adds; } }

    private Dictionary<FileRecord, FileRecord> moves;

    private List<FileRecord> deletes;
    public List<FileRecord> Deletes { get { return deletes; } }

    private List<FileRecord> edits;
    public List<FileRecord> Edits { get { return edits; } }

    /// <summary>
    /// Compare the old list of files with the new list in order to
    /// determine which files had been added, removed, moved, or edited.
    /// </summary>
    public void Compare()
    {
      // build dictionaries of file names for fast lookup
      Dictionary<string, FileRecord> oldFileNames = new Dictionary<string, FileRecord>();
      foreach (FileRecord r in oldFiles)
        oldFileNames[r.Name.ToLower()] = r;
      Dictionary<string, FileRecord> seenFileNames = new Dictionary<string, FileRecord>();
      foreach (FileRecord r in seenFiles)
        seenFileNames[r.Name.ToLower()] = r;

      // build list of new files we haven't seen before (adds)
      adds = new List<FileRecord>();
      foreach (FileRecord r in seenFiles)
        if (!oldFileNames.ContainsKey(r.Name.ToLower()))
          adds.Add(r);

      // build list of old files we don't see now (deletes)
      deletes = new List<FileRecord>();
      foreach (FileRecord r in oldFiles)
        if (!seenFileNames.ContainsKey(r.Name.ToLower()))
          deletes.Add(r);
      
      // some of the files in add/delete list might actually be moves, check for that
      moves = new Dictionary<FileRecord, FileRecord>();
      foreach (FileRecord a in adds) {
        byte[] hashCode = null;
        if (a.Length > 0)
          foreach (FileRecord d in deletes)
            if (a.Length == d.Length && a.LastWriteTime == d.LastWriteTime) {
              // probably the same file, but we need to check the hash to be sure
              if (hashCode == null)
                hashCode = ComputeHash(a);
              if (Utils.HashCodesMatch(hashCode, d.HashCode)) {
                LogFile.Log("{0} moved to {1}", Utils.MakeFullPath(root, d.Name),
                  Utils.MakeFullPath(root, a.Name));
                moves[d] = a;
              }
            }
      }
      // remove the moved files from the add and delete lists
      foreach (var kvp in moves) {
        deletes.Remove(kvp.Key);
        adds.Remove(kvp.Value);
      }

      // now check for edits
      edits = new List<FileRecord>();
      foreach (FileRecord o in oldFiles) {
        FileRecord n;
        if (seenFileNames.TryGetValue(o.Name.ToLower(), out n)) {
          if (o.Length != n.Length)
            edits.Add(o); // trivial case, length changed
          else if (o.CreationTime != n.CreationTime || o.LastWriteTime != n.LastWriteTime) {
            // probable edit, compare hash codes to be sure
            if (!Utils.HashCodesMatch(o.HashCode, ComputeHash(n)))
              edits.Add(n); // add the "new" version of the file to this list, because that's what gets saved later
          }

        }
      }

      LogFile.Log("Adds: {0} Deletes: {1} Moves: {2} Edits: {3}", adds.Count,
        deletes.Count, moves.Count, edits.Count);

    }

    /// <summary>
    /// Process moves by updating their records in oldFiles to reflect the new locations
    /// </summary>
    public void ProcessMoves()
    {
      if (moves.Count == 0)
        return;
      LogFile.Log("Processing moves for {0}...", root);
      foreach (var kvp in moves) {
        FileRecord r = kvp.Key; // entry in oldFiles list
        r.Name = kvp.Value.Name;
      }
      // save updated oldFiles list
      SaveFileList(oldFiles);
      // clear moves list, don't need it anymore
      moves.Clear();
    }

    /// <summary>
    /// Removes the file from the newFiles list and saves the new list to disk
    /// </summary>
    public void RemoveFile(FileRecord r)
    {
      Debug.Assert(deletes.Contains(r) || edits.Contains(r));
      newFiles.Remove(r);
      SaveFileList(newFiles);
    }

    /// <summary>
    /// Adds the file to newFiles and saves the new list to disk
    /// </summary>
    public void AddFile(FileRecord r)
    {
      UInt32 endBlock = r.StartBlock + r.LengthInBlocks;
      if (endBlock > MaxBlock)
        MaxBlock = endBlock;
      newFiles.Add(r);
      SaveFileList(newFiles);
    }

    private byte[] ComputeHash(FileRecord r)
    {
      using (FileStream s = new FileStream(Utils.MakeFullPath(root, r.Name), FileMode.Open, FileAccess.Read))
        hash.ComputeHash(s);
      return hash.Hash;
    }

    /// <summary>
    /// Returns true if there was a files.dat file present for this drive
    /// </summary>
    public bool HasFileData()
    {
      return (oldFiles != null);
    }

    /// <summary>
    /// Specifies whether files and folders with the Hidden attribute set should
    /// be ignored
    /// </summary>
    public bool IgnoreHidden { get; set; }

    private UInt32 enumBlock;
    private FileStream enumFile;
    private List<FileRecord>.Enumerator enumerator;

    public void BeginFileEnum()
    {
      enumBlock = 0;
      enumFile = null;
      enumerator = seenFiles.GetEnumerator();
    }

    public bool GetNextBlock(byte[] buf)
    {
      if (enumFile == null) {
        if (!enumerator.MoveNext())
          return false;
        // TODO: Handle zero-length file here?
        string fullName = Utils.MakeFullPath(root, enumerator.Current.Name);
        try {
          enumFile = new FileStream(fullName, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception e) {
          LogFile.Log("Error opening {0} for reading: {1}", fullName, e.Message);
          enumerator.Current.Skipped = true;
          enumerator.MoveNext();
          return GetNextBlock(buf);
        }
        LogFile.Log("Reading {0}", fullName);
        hash.Initialize();
        enumerator.Current.StartBlock = enumBlock;
      }
      int bytesRead = enumFile.Read(buf, 0, buf.Length);
      if (enumFile.Position < enumFile.Length)
        hash.TransformBlock(buf, 0, bytesRead, buf, 0);
      else {
        // reached end of this file
        hash.TransformFinalBlock(buf, 0, bytesRead);
        enumerator.Current.HashCode = hash.Hash;
        AppendFileRecord(enumerator.Current);
        enumFile.Close();
        enumFile.Dispose();
        enumFile = null;
        enumBlock += enumerator.Current.LengthInBlocks;
        Array.Clear(buf, bytesRead, buf.Length - bytesRead);
      }
      return true;
    }    

    public void EndFileEnum()
    {
      enumerator.Dispose();
    }

    public bool ReadBlock(UInt32 block, byte[] data)
    {
      FileRecord r = FindFileContaining(block);
      if (r == null)
        return false;
      string fullPath = r.FullPath;
      if (!File.Exists(fullPath))
        return false;
      // to do: what if the file has been edited?
      // Allow any I/O exceptions below to be caught by parent
      using (FileStream f = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read)) {
        f.Position = (block - r.StartBlock) * Parity.BlockSize;
        int bytesRead = f.Read(data, 0, data.Length);
        while (bytesRead < data.Length)
          data[bytesRead++] = 0;
        f.Close();
      }
      return true;
    }

    /// <summary>
    /// Returns a mask of used/unused blocks for this drive
    /// </summary>
    public bool[] BlockMask
    {
      get
      {
        bool[] blockMask = new bool[MaxBlock];
        foreach (FileRecord r in newFiles) {
          UInt32 endBlock = r.StartBlock + r.LengthInBlocks;
          for (UInt32 i = r.StartBlock; i < endBlock; i++)
            blockMask[i] = true;
        }
        return blockMask;
      }
    }

    private FileRecord FindFileContaining(UInt32 block)
    {
      if (block < MaxBlock)
        foreach (FileRecord r in oldFiles)
          if (r.ContainsBlock(block))
            return r;
      return null;
    }

    private void SaveFileList(IEnumerable<FileRecord> fileList)
    {
      DateTime start = DateTime.Now;
      LogFile.VerboseLog("Saving file data for {0}...", root);
      string backup = "";
      if (File.Exists(metaFileName)) {
        backup = metaFileName + ".BAK";
        File.Move(metaFileName, backup);
      }
      using (FileStream f = new FileStream(metaFileName, FileMode.Create, FileAccess.Write)) {
        FileRecord.WriteUInt32(f, META_FILE_VERSION);
        foreach (FileRecord r in fileList)
          r.WriteToFile(f);
        f.Close();
      }
      if (backup != "")
        File.Delete(backup);
      TimeSpan elapsed = DateTime.Now - start;
      LogFile.VerboseLog("{0} records saved in {1:F2} sec",
        oldFiles.Length, elapsed.TotalSeconds);
    }

    private void AppendFileRecord(FileRecord r)
    {
      if (!File.Exists(metaFileName))
        using (FileStream fNew = new FileStream(metaFileName, FileMode.Create, FileAccess.Write))
          FileRecord.WriteUInt32(fNew, META_FILE_VERSION);
      using (FileStream f = new FileStream(metaFileName, FileMode.Append, FileAccess.Write))
        r.WriteToFile(f); 
    }
    
    /// <summary>
    /// Loads the files.dat file containing the record of any existing protected file data
    /// </summary>
    private void LoadFileData()
    {
      using (FileStream metaData = new FileStream(metaFileName, FileMode.Open, FileAccess.Read)) {
        UInt32 version = FileRecord.ReadUInt32(metaData);
        if (version == 1)
          // skip past unused count field
          FileRecord.ReadUInt32(metaData);
        else if (version != META_FILE_VERSION)
          throw new Exception("file version mismatch: " + metaFileName);
        List<FileRecord> records = new List<FileRecord>();
        while (metaData.Position < metaData.Length)
          records.Add(FileRecord.LoadFromFile(metaData, this));
        oldFiles = records.ToArray();
        newFiles = records; // newFiles starts out as a copy of oldFiles
      }
      CalculateMaxBlock();
    }

    /// <summary>
    /// Determines the index of the first unused 64K parity block for this drive.
    /// </summary>
    private void CalculateMaxBlock()
    {
      MaxBlock = 0;
      foreach (FileRecord r in newFiles) {
        UInt32 endBlock = r.StartBlock + r.LengthInBlocks;
        if (endBlock > MaxBlock)
          MaxBlock = endBlock;
      }
    }

    /// <summary>
    /// Generates a "free list" of unused blocks in the existing parity data 
    /// for this drive which we can then re-use for adds, so that we don't grow 
    /// the parity data unnecessarily.
    /// </summary>
    public List<FreeNode> GetFreeList()
    {
      bool[] blockMask = BlockMask;

      List<FreeNode> freeList = new List<FreeNode>();
      UInt32 block = 0;
      while (block < MaxBlock)
        if (!blockMask[block]) {
          FreeNode n = new FreeNode();
          n.Start = block++;
          n.Length = 1;
          while (block < MaxBlock && (!blockMask[block])) {
            n.Length++;
            block++;
          }
          freeList.Add(n);
        }
        else
          block++;

      return freeList;
    }
  }

  public class FreeNode
  {
    public UInt32 Start { get; set; }
    public UInt32 Length { get; set; }  // in blocks
    public const UInt32 INVALID_BLOCK = 0xFFFFFFFF;

    public static UInt32 FindBest(List<FreeNode> list, UInt32 blocks)
    {
      FreeNode best = null;
      foreach (FreeNode n in list)
        if (n.Length == blocks) {
          best = n;
          break;
        }
        else if (n.Length > blocks)
          if ((best == null) || (n.Length < best.Length))
            best = n;
      if (best == null)
        return INVALID_BLOCK;
      UInt32 result = best.Start;
      if (best.Length == blocks)
        list.Remove(best);
      else {
        best.Start += blocks;
        best.Length -= blocks;
      }
      return result;
    }

  }

}
