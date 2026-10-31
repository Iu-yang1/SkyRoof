using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace SkyRoof.SSTV
{
  /// <summary>
  /// File operations for SSTV image history. Kept separate from dialogs so
  /// imports, exports and deletions can be exercised in CI without WinForms.
  /// </summary>
  public static class SstvImageLibrary
  {
    private const long MaxImportBytes = 64L * 1024 * 1024;
    private const long MaxPixels = 100_000_000;

    /// <summary>
    /// Decode an external picture once, bound editing memory, and release the
    /// source file immediately. SSTV's raster is at most hundreds of pixels
    /// wide, so a 20-megapixel photo need not remain expanded in the editor.
    /// </summary>
    public static Bitmap LoadForEditing(string path, int maxDimension = 2048)
    {
      if (maxDimension < 320 || maxDimension > 4096)
        throw new ArgumentOutOfRangeException(nameof(maxDimension));
      if (!File.Exists(path)) throw new FileNotFoundException("图片不存在", path);
      if (new FileInfo(path).Length > MaxImportBytes)
        throw new InvalidDataException("图片文件过大");
      using var source = Image.FromFile(path);
      if ((long)source.Width * source.Height > MaxPixels)
        throw new InvalidDataException("图片分辨率过大");
      return ScaleForEditing(source, maxDimension);
    }

    public static Bitmap ScaleForEditing(Image source, int maxDimension = 2048)
    {
      if (source == null) throw new ArgumentNullException(nameof(source));
      if (maxDimension < 320 || maxDimension > 4096)
        throw new ArgumentOutOfRangeException(nameof(maxDimension));
      if ((long)source.Width * source.Height > MaxPixels)
        throw new InvalidDataException("图片分辨率过大");
      float scale = Math.Min(1f,
        Math.Min((float)maxDimension / source.Width, (float)maxDimension / source.Height));
      int width = Math.Max(1, (int)Math.Round(source.Width * scale));
      int height = Math.Max(1, (int)Math.Round(source.Height * scale));
      var result = new Bitmap(width, height, PixelFormat.Format32bppArgb);
      try
      {
        using var g = Graphics.FromImage(result);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, new Rectangle(0, 0, width, height));
        return result;
      }
      catch { result.Dispose(); throw; }
    }

    public static string Import(string folder, string sourcePath)
    {
      if (!File.Exists(sourcePath)) throw new FileNotFoundException("图片不存在", sourcePath);
      if (new FileInfo(sourcePath).Length > MaxImportBytes)
        throw new InvalidDataException("图片文件过大");
      using var original = Image.FromFile(sourcePath);
      if ((long)original.Width * original.Height > MaxPixels)
        throw new InvalidDataException("图片分辨率过大");
      Directory.CreateDirectory(folder);
      string destination = Path.Combine(Path.GetFullPath(folder),
        "SSTV_import_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") +
        "_" + Guid.NewGuid().ToString("N")[..8] + ".png");
      // A validated PNG can be copied byte-for-byte without an expensive
      // lossless re-encode, preserving ancillary data and speeding imports.
      if (original.RawFormat.Guid == ImageFormat.Png.Guid)
        File.Copy(sourcePath, destination);
      else
      {
        using var image = ScaleForEditing(original);
        image.Save(destination, ImageFormat.Png);
      }
      return destination;
    }

    public static void Export(string libraryFile, string outputPath)
    {
      if (!File.Exists(libraryFile))
        throw new FileNotFoundException("图库图片不存在", libraryFile);
      string source = Path.GetFullPath(libraryFile);
      string destination = Path.GetFullPath(outputPath);
      if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
        return;
      File.Copy(source, destination, overwrite: true);
    }

    /// <summary>
    /// Only delete images physically inside this library directory.
    /// A matching optional receive-metadata JSON file is removed as well.
    /// </summary>
    public static bool Delete(string folder, string libraryFile)
    {
      string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar);
      string file = Path.GetFullPath(libraryFile);
      if (!string.Equals(Path.GetDirectoryName(file)?.TrimEnd(
          Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
          root, StringComparison.OrdinalIgnoreCase) ||
          !string.Equals(Path.GetExtension(file), ".png", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("只能删除图库目录内的 PNG 图片");
      if (!File.Exists(file)) return false;
      File.Delete(file);
      string metadata = Path.ChangeExtension(file, ".json");
      if (File.Exists(metadata)) File.Delete(metadata);
      return true;
    }
  }
}
