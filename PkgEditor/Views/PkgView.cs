﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Data;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using System.Collections;
using System.IO;

using LibOrbisPkg.PKG;
using LibOrbisPkg.Util;
using System.Threading.Tasks;
using System.IO.MemoryMappedFiles;
using LibOrbisPkg.PFS;
using LibOrbisPkg.SFO;

namespace PkgEditor.Views
{
  public partial class PkgView : View
  {
    public override bool CanSave => false;
    public override bool CanSaveAs => false;

    public override void Close()
    {
      pkgFile.Dispose();
      va?.Dispose();
    }

    private Pkg pkg;
    private MemoryMappedFile pkgFile;
    private MemoryMappedViewAccessor va;
    private string passcode;

    public PkgView(string path)
    {
      InitializeComponent();
      if (path == null) return;
      pkgFile = MemoryMappedFile.CreateFromFile(path);
      using (var s = pkgFile.CreateViewStream())
        ObjectPreview(new PkgReader(s).ReadHeader(), pkgHeaderTreeView);
      using (var s = pkgFile.CreateViewStream())
        pkg = new PkgReader(s).ReadPkg();
      try
      {
        using (var s = pkgFile.CreateViewStream((long)pkg.Header.pfs_image_offset, (long)pkg.Header.pfs_image_size))
          ObjectPreview(PfsHeader.ReadFromStream(s), pfsHeaderTreeView);
      }
      catch(Exception e)
      {
        pkgHeaderTabControl.TabPages.Remove(outerPfsHeaderTabPage);
        MessageBox.Show("Error loading outer PFS: " + e.Message + Environment.NewLine + "Please report this issue at https://github.com/maxton/LibOrbisPkg/issues");
      }
      if (pkg.Metas.Metas.Where(entry => entry.id == EntryId.ICON0_PNG).FirstOrDefault() is MetaEntry icon0)
      {
        using(var s = pkgFile.CreateViewStream(icon0.DataOffset, icon0.DataSize))
        {
          pictureBox1.Image = Image.FromStream(s);
        }
      }
      contentIdTextBox.Text = pkg.Header.content_id;
      titleTextBox.Text = pkg.ParamSfo.ParamSfo["TITLE"]?.ToString();
      sizeLabel.Text = FileView.HumanReadableFileSize((long)pkg.Header.package_size);
      var category = pkg.ParamSfo.ParamSfo["CATEGORY"].ToString();
      typeLabel.Text = SFOView.SfoTypes.Where(x => x.Category == category).FirstOrDefault() is SFOView.SfoType t ? t.Description : "Unknown";
      versionLabel.Text = pkg.ParamSfo.ParamSfo["VERSION"]?.ToString();
      if(pkg.ParamSfo.ParamSfo["APP_VER"] is Utf8Value v)
      {
        appVerLabel.Text = v.Value;
      }
      else
      {
        appVerLabelLabel.Visible = false;
        appVerLabel.Visible = false;
      }
      var sfoEditor = new SFOView(pkg.ParamSfo.ParamSfo, true);
      sfoEditor.Dock = DockStyle.Fill;
      tabPage1.Controls.Add(sfoEditor);


      if (pkg.CheckPasscode("00000000000000000000000000000000"))
      {
        passcode = "00000000000000000000000000000000";
        ekpfs = Crypto.ComputeKeys(pkg.Header.content_id, passcode, 1);
      }
      else
      {
        ekpfs = pkg.GetEkpfs();
      }
      ReopenFileView();

      foreach(var e in pkg.Metas.Metas)
      {
        var lvi = new ListViewItem(new[] {
          e.id.ToString(),
          string.Format("0x{0:X}", e.DataSize),
          string.Format("0x{0:X}", e.DataOffset),
          e.Encrypted ? "Yes" : "No",
          e.KeyIndex.ToString(),
        });
        lvi.Tag = e;
        entriesListView.Items.Add(lvi);
      }
    }

    void ObjectPreview(object obj, TreeView tv)
    {
      tv.Nodes.Clear();
      AddObjectNodes(obj, tv.Nodes);
    }

    string toString(object obj)
    {
      switch (obj)
      {
        case Byte _:
        case UInt16 _:
        case UInt32 _:
        case UInt64 _:
        case Int16 _:
        case Int32 _:
        case Int64 _:
          return string.Format("0x{0:X}", obj);
        default:
          return obj.ToString();
      }
    }

    /// <summary>
    /// Adds the given object's public fields to the given TreeNodeCollection.
    /// </summary>
    void AddObjectNodes(object obj, TreeNodeCollection nodes)
    {
      if (obj == null) return;
      var fields = obj.GetType().GetFields();
      foreach (var f in fields)
      {
        if (f.IsLiteral) continue;
        var val = f.GetValue(obj);
        if (val is byte[] b)
        {
          nodes.Add(f.Name + " = " + LibOrbisPkg.Util.Crypto.AsHexCompact(b));
        }
        else if (f.FieldType.IsPrimitive || f.FieldType == typeof(string) || f.FieldType.IsEnum)
        {
          if (val != null)
          {
            nodes.Add(f.Name + " = " + toString(val));
          }
        }
        else if (f.FieldType.IsArray)
        {
          AddArrayNodes(f.GetValue(obj) as Array, f.Name, nodes);
        }
        else if (f.FieldType.IsGenericType && f.FieldType.GetGenericTypeDefinition() == typeof(List<>))
        {
          var internalType = f.FieldType.GetGenericArguments()[0];
          AddArrayNodes((f.GetValue(obj) as IList).Cast<object>().ToArray(), f.Name, nodes);
        }
        else
        {
          var node = new TreeNode(f.Name);
          AddObjectNodes(f.GetValue(obj), node.Nodes);
          nodes.Add(node);
        }
      }
    }

    /// <summary>
    /// Adds the given array to the given TreeNodeCollection.
    /// </summary>
    void AddArrayNodes(Array arr, string name, TreeNodeCollection nodes)
    {
      var node = new TreeNode($"{name} ({arr.Length})");
      var eType = arr.GetType().GetElementType();
      if (eType.IsPrimitive || eType == typeof(string) || eType.IsEnum)
        for (var i = 0; i < arr.Length; i++)
        {
          var n = new TreeNode($"{name}[{i}] = {toString(arr.GetValue(i))}");
          node.Nodes.Add(n);
        }
      else for (var i = 0; i < arr.Length; i++)
        {
          var myName = $"{name}[{i}]";
          if (eType.IsArray)
            AddArrayNodes(arr.GetValue(i) as Array, myName, node.Nodes);
          else
          {
            var obj = arr.GetValue(i);

            System.Reflection.FieldInfo nameField;
            if (null != (nameField = obj.GetType().GetField("Name")))
            {
              myName += $" (Name: {nameField.GetValue(obj)})";
            }
            var n = new TreeNode(myName);
            var item = arr.GetValue(i);
            AddObjectNodes(item, n.Nodes);
            node.Nodes.Add(n);
          }
        }
      nodes.Add(node);
    }

    private byte[] ekpfs;

    private void ReopenFileView()
    {
      if (!pkg.CheckEkpfs(ekpfs))
        return;
      if (va != null)
        return;
      try
      {
        va = pkgFile.CreateViewAccessor((long)pkg.Header.pfs_image_offset, (long)pkg.Header.pfs_image_size);
        var outerPfs = new PfsReader(va, pkg.Header.pfs_flags, ekpfs);
        var inner = new PfsReader(new PFSCReader(outerPfs.GetFile("pfs_image.dat").GetView()));
        var view = new FileView(inner);
        view.Dock = DockStyle.Fill;
        filesTab.Controls.Clear();
        filesTab.Controls.Add(view);
      }
      catch(Exception)
      {
        va?.Dispose();
        va = null;
      }
    }

    private void button1_Click(object sender, EventArgs e)
    {
      if(pkg.CheckPasscode(passcodeTextBox.Text))
      {
        passcode = passcodeTextBox.Text;
        ekpfs = Crypto.ComputeKeys(pkg.Header.content_id, passcode, 1);
        ReopenFileView();
      }
      else
      {
        MessageBox.Show("Invalid passcode!");
      }
    }

    private void entriesListView_DoubleClick(object sender, EventArgs e)
    {
      if(entriesListView.SelectedItems[0].SubItems[0].Text == "PARAM_SFO")
      {
        mainWin.OpenTab(new SFOView(pkg.ParamSfo.ParamSfo, true), "param.sfo [Read-Only]");
      }
    }

    private async void checkDigests_Click(object sender, EventArgs _)
    {
      validateResult.Text = "Checking PKG digests...";
      listView1.Enabled = false;
      checkDigestsButton.Enabled = false;
      var validator = new PkgValidator(pkg);
      listView1.Items.Clear();
      await Task.Run(() =>
      {
        using (var s = pkgFile.CreateViewStream())
        {
          foreach (var v in validator.Validate(s).OrderBy((a)=>a.Item1.Location))
          {
            var item = new ListViewItem(v.Item1.Name);
            if (v.Item2 == PkgValidator.ValidationResult.Ok)
              item.BackColor = Color.LightGreen;
            else if (v.Item2 == PkgValidator.ValidationResult.Fail)
              item.BackColor = Color.LightSalmon;
            item.Tag = v;
            listView1.BeginInvoke((Action)(() => listView1.Items.Add(item)));
          }
        }
      });

      listView1.Enabled = true;
      checkDigestsButton.Enabled = true;
      validateResult.Text = "Done!";
      foreach(ListViewItem x in listView1.Items)
      {
        if(x.BackColor == Color.LightSalmon)
        {
          validateResult.Text += Environment.NewLine + "Failed: " + x.Text;
        }
      }
    }

    private void ListView1_SelectedIndexChanged(object sender, EventArgs e)
    {
      if(listView1.SelectedItems.Count != 1)
      {
        validateResult.Text = "";
      }
      else
      {
        if(listView1.SelectedItems[0].Tag is Tuple<PkgValidator.Validation, PkgValidator.ValidationResult> t)
        {
          var resultTxt = "";
          switch (t.Item2)
          {
            case PkgValidator.ValidationResult.Ok:
              resultTxt = "This was validated successfully";
              break;
            case PkgValidator.ValidationResult.Fail:
              resultTxt = "This was found to be invalid";
              break;
            case PkgValidator.ValidationResult.NoKey:
              resultTxt = "This may be valid but could not be checked due to lack of necessary key(s)";
              break;
          }
          validateResult.Text =
            $"Type: {t.Item1.Type}{Environment.NewLine}" +
            $"Name: {t.Item1.Name}{Environment.NewLine}" +
            $"Description: {t.Item1.Description}{Environment.NewLine}" +
            $"Offset: 0x{t.Item1.Location:X}{Environment.NewLine}" +
           resultTxt;
        }
      }
    }

    bool RequestPasscode()
    {
      if(new PasscodeEntry() is PasscodeEntry p && p.ShowDialog() == DialogResult.OK && pkg.CheckPasscode(p.Passcode))
      {
        passcode = p.Passcode;
        ekpfs = Crypto.ComputeKeys(pkg.Header.content_id, passcode, 1);
        ReopenFileView();
        return true;
      }
      return false;
    }

    void ExtractEntry(MetaEntry entry, bool decrypt = false)
    {
      if (decrypt && entry.Encrypted && passcode == null)
      {
        var gotPasscode = false;
        while (gotPasscode == false)
        {
          gotPasscode = RequestPasscode();
          if (gotPasscode) break;
          var result = MessageBox.Show(
            "Sorry, that passcode was incorrect." + Environment.NewLine
            + "Abort to cancel extraction, ignore to extract in encrypted form, retry to try a new passcode.",
            "Invalid Passcode",
            MessageBoxButtons.AbortRetryIgnore,
            MessageBoxIcon.Warning);
          if (result == DialogResult.Abort)
            return;
          if (result == DialogResult.Ignore)
            break;
        }
      }
      var name = entry.NameTableOffset != 0 ? pkg.EntryNames.GetName(entry.NameTableOffset) : entry.id.ToString();
      if (new SaveFileDialog() { FileName = name } is SaveFileDialog s && s.ShowDialog() == DialogResult.OK)
      {
        using (var f = File.OpenWrite(s.FileName))
        using (var entryStream = pkgFile.CreateViewStream(entry.DataOffset, entry.DataSize))
        {
          if(entry.Encrypted && decrypt && passcode != null)
          {
            var tmp = new byte[(entry.DataSize + 15) & ~15];
            entryStream.Read(tmp, 0, tmp.Length);
            tmp = Entry.Decrypt(tmp, pkg.Header.content_id, passcode, entry);
            f.Write(tmp, 0, (int)entry.DataSize);
          }
          else
          {
            entryStream.CopyTo(f);
          }
        }
      }
    }

    private void ExtractToolStripMenuItem_Click(object sender, EventArgs e)
    {
      if(entriesListView.SelectedItems.Count == 1)
      {
        ExtractEntry(entriesListView.SelectedItems[0].Tag as MetaEntry);       
      }
    }
    private void ToolStripMenuItem1_Click(object sender, EventArgs e)
    {
      if (entriesListView.SelectedItems.Count == 1)
      {
        ExtractEntry(entriesListView.SelectedItems[0].Tag as MetaEntry, true);
      }
    }

    private void ContextMenuStrip1_Opening(object sender, CancelEventArgs e)
    {
      if (entriesListView.SelectedItems.Count != 1)
      {
        extractDecryptedMenuItem.Enabled = extractToolStripMenuItem.Enabled = false;
      }
      else
      {
        extractToolStripMenuItem.Enabled = true;
        extractDecryptedMenuItem.Enabled = (entriesListView.SelectedItems[0].Tag as MetaEntry)?.Encrypted ?? false;
      }
    }

    private void Button2_Click(object sender, EventArgs e)
    {
      var sfd = new SaveFileDialog()
      {
        FileName = "Project.gp4",
        Filter = "GP4 Projects|Project.gp4",
        Title = "Choose a location for the exported PKG and project file",
      };
      if(sfd.ShowDialog() == DialogResult.OK)
      {
        var outputDir = Path.GetDirectoryName(sfd.FileName);
        LibOrbisPkg.GP4.Gp4Creator.CreateProjectFromPKG(outputDir, pkgFile, passcode);
        MessageBox.Show("PKG Exported to " + outputDir);
      }
    }
  }
}
